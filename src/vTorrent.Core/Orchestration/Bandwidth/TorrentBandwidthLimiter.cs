using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace vTorrent.Core.Orchestration.Bandwidth;

/// <summary>
/// Per-torrent bandwidth limiter that coordinates with global BandwidthManagers.
/// Provides a simplified interface for TorrentEngine and PeerConnection to use.
/// </summary>
public class TorrentBandwidthLimiter : IDisposable
{
    private readonly BandwidthManager _downloadManager;
    private readonly BandwidthManager _uploadManager;
    private readonly ILogger<TorrentBandwidthLimiter>? _logger;

    /// <summary>
    /// Per-torrent download channel (null = use global only).
    /// </summary>
    public BandwidthChannel? TorrentDownloadChannel { get; private set; }

    /// <summary>
    /// Per-torrent upload channel (null = use global only).
    /// </summary>
    public BandwidthChannel? TorrentUploadChannel { get; private set; }

    /// <summary>
    /// Info hash of the torrent (for identification).
    /// </summary>
    public string InfoHash { get; }

    /// <summary>
    /// Creates a new torrent bandwidth limiter.
    /// </summary>
    /// <param name="infoHash">Torrent info hash</param>
    /// <param name="downloadManager">Global download bandwidth manager</param>
    /// <param name="uploadManager">Global upload bandwidth manager</param>
    /// <param name="downloadLimit">Per-torrent download limit (0 = use global)</param>
    /// <param name="uploadLimit">Per-torrent upload limit (0 = use global)</param>
    /// <param name="logger">Optional logger</param>
    public TorrentBandwidthLimiter(
        string infoHash,
        BandwidthManager downloadManager,
        BandwidthManager uploadManager,
        int downloadLimit = 0,
        int uploadLimit = 0,
        ILogger<TorrentBandwidthLimiter>? logger = null)
    {
        InfoHash = infoHash ?? throw new ArgumentNullException(nameof(infoHash));
        _downloadManager = downloadManager ?? throw new ArgumentNullException(nameof(downloadManager));
        _uploadManager = uploadManager ?? throw new ArgumentNullException(nameof(uploadManager));
        _logger = logger;

        SetDownloadLimit(downloadLimit);
        SetUploadLimit(uploadLimit);
    }

    /// <summary>
    /// Sets the per-torrent download limit.
    /// </summary>
    /// <param name="bytesPerSecond">Limit in bytes/sec (0 = unlimited/use global)</param>
    public void SetDownloadLimit(int bytesPerSecond)
    {
        if (bytesPerSecond > 0)
        {
            TorrentDownloadChannel ??= new BandwidthChannel($"torrent_{InfoHash[..8]}_dl", bytesPerSecond);
            TorrentDownloadChannel.Throttle = bytesPerSecond;
            _logger?.LogDebug("Torrent {Hash} download limit set to {Limit} B/s", InfoHash[..8], bytesPerSecond);
        }
        else
        {
            TorrentDownloadChannel = null;
        }
    }

    /// <summary>
    /// Sets the per-torrent upload limit.
    /// </summary>
    /// <param name="bytesPerSecond">Limit in bytes/sec (0 = unlimited/use global)</param>
    public void SetUploadLimit(int bytesPerSecond)
    {
        if (bytesPerSecond > 0)
        {
            TorrentUploadChannel ??= new BandwidthChannel($"torrent_{InfoHash[..8]}_ul", bytesPerSecond);
            TorrentUploadChannel.Throttle = bytesPerSecond;
            _logger?.LogDebug("Torrent {Hash} upload limit set to {Limit} B/s", InfoHash[..8], bytesPerSecond);
        }
        else
        {
            TorrentUploadChannel = null;
        }
    }

    /// <summary>
    /// Requests download bandwidth for a consumer.
    /// </summary>
    /// <param name="consumer">The bandwidth consumer</param>
    /// <param name="bytes">Bytes requested</param>
    /// <param name="priority">Priority (1-255)</param>
    /// <returns>Bytes granted immediately (0 if queued)</returns>
    public int RequestDownload(IBandwidthConsumer consumer, int bytes, int priority = 128)
    {
        return _downloadManager.RequestBandwidth(consumer, bytes, priority, TorrentDownloadChannel);
    }

    /// <summary>
    /// Requests upload bandwidth for a consumer.
    /// </summary>
    /// <param name="consumer">The bandwidth consumer</param>
    /// <param name="bytes">Bytes requested</param>
    /// <param name="priority">Priority (1-255)</param>
    /// <returns>Bytes granted immediately (0 if queued)</returns>
    public int RequestUpload(IBandwidthConsumer consumer, int bytes, int priority = 128)
    {
        return _uploadManager.RequestBandwidth(consumer, bytes, priority, TorrentUploadChannel);
    }

    /// <summary>
    /// Cancels all pending requests for a consumer.
    /// </summary>
    /// <param name="consumer">The consumer to remove</param>
    public void CancelRequests(IBandwidthConsumer consumer)
    {
        _downloadManager.CancelRequests(consumer);
        _uploadManager.CancelRequests(consumer);
    }

    /// <summary>
    /// Gets the effective download limit (considering global and per-torrent).
    /// </summary>
    public int EffectiveDownloadLimit
    {
        get
        {
            int global = _downloadManager.GlobalChannel.Throttle;
            int torrent = TorrentDownloadChannel?.Throttle ?? 0;

            if (global == 0 && torrent == 0) return 0; // Unlimited
            if (global == 0) return torrent;
            if (torrent == 0) return global;
            return Math.Min(global, torrent);
        }
    }

    /// <summary>
    /// Gets the effective upload limit (considering global and per-torrent).
    /// </summary>
    public int EffectiveUploadLimit
    {
        get
        {
            int global = _uploadManager.GlobalChannel.Throttle;
            int torrent = TorrentUploadChannel?.Throttle ?? 0;

            if (global == 0 && torrent == 0) return 0; // Unlimited
            if (global == 0) return torrent;
            if (torrent == 0) return global;
            return Math.Min(global, torrent);
        }
    }

    /// <summary>
    /// Checks if download is rate limited.
    /// </summary>
    public bool IsDownloadLimited => EffectiveDownloadLimit > 0;

    /// <summary>
    /// Checks if upload is rate limited.
    /// </summary>
    public bool IsUploadLimited => EffectiveUploadLimit > 0;

    public void Dispose()
    {
        // Channels are not owned by this class, so don't dispose them
        // Just clear references
        TorrentDownloadChannel = null;
        TorrentUploadChannel = null;
    }
}

/// <summary>
/// Simple adapter for PeerConnection to implement IBandwidthConsumer.
/// </summary>
public class PeerBandwidthConsumer : IBandwidthConsumer
{
    private readonly string _id;
    private readonly Func<bool> _isDisconnecting;
    private readonly Action<BandwidthChannelType, int> _onAssigned;

    /// <summary>
    /// Current download quota available.
    /// </summary>
    public int DownloadQuota { get; private set; }

    /// <summary>
    /// Current upload quota available.
    /// </summary>
    public int UploadQuota { get; private set; }

    /// <summary>
    /// Whether a download request is pending.
    /// </summary>
    public bool DownloadPending { get; set; }

    /// <summary>
    /// Whether an upload request is pending.
    /// </summary>
    public bool UploadPending { get; set; }

    public string Id => _id;
    public bool IsDisconnecting => _isDisconnecting();

    public PeerBandwidthConsumer(
        string id,
        Func<bool> isDisconnecting,
        Action<BandwidthChannelType, int>? onAssigned = null)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _isDisconnecting = isDisconnecting ?? throw new ArgumentNullException(nameof(isDisconnecting));
        _onAssigned = onAssigned ?? ((_, __) => { });
    }

    public void OnBandwidthAssigned(BandwidthChannelType channel, int amount)
    {
        if (channel == BandwidthChannelType.Download)
        {
            DownloadQuota += amount;
            DownloadPending = false;
        }
        else
        {
            UploadQuota += amount;
            UploadPending = false;
        }

        _onAssigned(channel, amount);
    }

    /// <summary>
    /// Consumes download quota.
    /// </summary>
    /// <param name="bytes">Bytes to consume</param>
    public void ConsumeDownload(int bytes)
    {
        DownloadQuota = Math.Max(0, DownloadQuota - bytes);
    }

    /// <summary>
    /// Consumes upload quota.
    /// </summary>
    /// <param name="bytes">Bytes to consume</param>
    public void ConsumeUpload(int bytes)
    {
        UploadQuota = Math.Max(0, UploadQuota - bytes);
    }

    /// <summary>
    /// Resets quota (for disconnection).
    /// </summary>
    public void Reset()
    {
        DownloadQuota = 0;
        UploadQuota = 0;
        DownloadPending = false;
        UploadPending = false;
    }
}
