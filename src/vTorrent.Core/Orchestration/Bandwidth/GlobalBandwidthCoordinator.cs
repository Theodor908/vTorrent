using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Core.Orchestration.Bandwidth;

/// <summary>
/// Global bandwidth coordinator that manages download and upload bandwidth managers.
/// Provides periodic quota distribution and settings updates.
/// </summary>
public class GlobalBandwidthCoordinator : IDisposable
{
    private readonly ILogger<GlobalBandwidthCoordinator> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BandwidthManager _downloadManager;
    private readonly BandwidthManager _uploadManager;
    private readonly ConcurrentDictionary<string, TorrentBandwidthLimiter> _torrentLimiters = new();
    private readonly IOptionsMonitor<BandwidthSettings>? _bandwidthOptions;

    private Timer? _updateTimer;
    private bool _disposed;
    private MixedModeAlgorithm _mixedMode = MixedModeAlgorithm.PeerProportional;

    /// <summary>
    /// Update interval in milliseconds.
    /// </summary>
    public const int DefaultUpdateIntervalMs = 100;

    /// <summary>
    /// Gets the download bandwidth manager.
    /// </summary>
    public BandwidthManager DownloadManager => _downloadManager;

    /// <summary>
    /// Gets the upload bandwidth manager.
    /// </summary>
    public BandwidthManager UploadManager => _uploadManager;

    /// <summary>
    /// Creates a new global bandwidth coordinator.
    /// </summary>
    /// <param name="loggerFactory">Logger factory</param>
    /// <param name="globalDownloadLimit">Global download limit (0 = unlimited)</param>
    /// <param name="globalUploadLimit">Global upload limit (0 = unlimited)</param>
    public GlobalBandwidthCoordinator(
        ILoggerFactory loggerFactory,
        int globalDownloadLimit = 0,
        int globalUploadLimit = 0,
        IOptionsMonitor<BandwidthSettings>? bandwidthOptions = null)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<GlobalBandwidthCoordinator>();
        _bandwidthOptions = bandwidthOptions;

        _downloadManager = new BandwidthManager(
            BandwidthChannelType.Download,
            globalDownloadLimit,
            loggerFactory.CreateLogger<BandwidthManager>());

        _uploadManager = new BandwidthManager(
            BandwidthChannelType.Upload,
            globalUploadLimit,
            loggerFactory.CreateLogger<BandwidthManager>());

        if (bandwidthOptions != null)
            _mixedMode = bandwidthOptions.CurrentValue.MixedModeAlgorithm;

        _logger.LogDebug("GlobalBandwidthCoordinator created with DL={DL}B/s, UL={UL}B/s",
            globalDownloadLimit, globalUploadLimit);
    }

    /// <summary>
    /// Starts the periodic quota distribution timer.
    /// </summary>
    /// <param name="intervalMs">Update interval in milliseconds</param>
    public void Start(int intervalMs = DefaultUpdateIntervalMs)
    {
        if (_updateTimer != null) return;

        _updateTimer = new Timer(
            OnTimerTick,
            null,
            intervalMs,
            intervalMs);

        _logger.LogDebug("Bandwidth coordinator started with {Interval}ms interval", intervalMs);
    }

    /// <summary>
    /// Stops the periodic quota distribution timer.
    /// </summary>
    public void Stop()
    {
        _updateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _updateTimer?.Dispose();
        _updateTimer = null;

        _logger.LogDebug("Bandwidth coordinator stopped");
    }

    private void OnTimerTick(object? state)
    {
        try
        {
            // TODO: Apply MixedModeSplitter.Calculate() per-torrent when iterating peer connections
            // Currently bandwidth is distributed uniformly. MixedMode split requires
            // per-transport-type quota buckets which will be integrated when the
            // per-peer bandwidth pipeline supports transport type awareness.
            _downloadManager.UpdateQuotas();
            _uploadManager.UpdateQuotas();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating bandwidth quotas");
        }
    }

    /// <summary>
    /// Manually triggers a quota update (useful for testing or immediate distribution).
    /// </summary>
    public void UpdateQuotas()
    {
        _downloadManager.UpdateQuotas();
        _uploadManager.UpdateQuotas();
    }

    /// <summary>
    /// Applies global settings.
    /// </summary>
    /// <param name="settings">Global settings</param>
    public void ApplySettings(GlobalSettings settings)
    {
        if (settings == null) return;

        // Global limits
        _downloadManager.SetGlobalLimit(settings.Bandwidth.GlobalDownloadLimit);
        _uploadManager.SetGlobalLimit(settings.Bandwidth.GlobalUploadLimit);

        // Default per-torrent limits
        _downloadManager.SetDefaultPerTorrentLimit(settings.Bandwidth.PerTorrentDownloadLimit);
        _uploadManager.SetDefaultPerTorrentLimit(settings.Bandwidth.PerTorrentUploadLimit);

        // MixedMode algorithm for TCP/uTP bandwidth split
        _mixedMode = settings.Bandwidth.MixedModeAlgorithm;

        _logger.LogDebug("Bandwidth settings applied: DL={DL}B/s, UL={UL}B/s, PerTorrent DL={PTD}B/s, UL={PTU}B/s",
            settings.Bandwidth.GlobalDownloadLimit,
            settings.Bandwidth.GlobalUploadLimit,
            settings.Bandwidth.PerTorrentDownloadLimit,
            settings.Bandwidth.PerTorrentUploadLimit);
    }

    /// <summary>
    /// Gets or creates a bandwidth limiter for a torrent.
    /// </summary>
    /// <param name="infoHash">Torrent info hash</param>
    /// <param name="downloadLimit">Per-torrent download limit (0 = use global default)</param>
    /// <param name="uploadLimit">Per-torrent upload limit (0 = use global default)</param>
    /// <returns>The torrent bandwidth limiter</returns>
    public TorrentBandwidthLimiter GetOrCreateLimiter(
        string infoHash,
        int downloadLimit = 0,
        int uploadLimit = 0)
    {
        return _torrentLimiters.GetOrAdd(infoHash, hash =>
        {
            var limiter = new TorrentBandwidthLimiter(
                hash,
                _downloadManager,
                _uploadManager,
                downloadLimit,
                uploadLimit,
                _loggerFactory.CreateLogger<TorrentBandwidthLimiter>());

            _logger.LogDebug("Created bandwidth limiter for torrent {Hash}", hash[..8]);
            return limiter;
        });
    }

    /// <summary>
    /// Removes a bandwidth limiter for a torrent.
    /// </summary>
    /// <param name="infoHash">Torrent info hash</param>
    public void RemoveLimiter(string infoHash)
    {
        if (_torrentLimiters.TryRemove(infoHash, out var limiter))
        {
            limiter.Dispose();
            _logger.LogDebug("Removed bandwidth limiter for torrent {Hash}", infoHash[..8]);
        }
    }

    /// <summary>
    /// Updates a torrent's bandwidth limits.
    /// </summary>
    /// <param name="infoHash">Torrent info hash</param>
    /// <param name="downloadLimit">New download limit (0 = use global default)</param>
    /// <param name="uploadLimit">New upload limit (0 = use global default)</param>
    public void UpdateTorrentLimits(string infoHash, int downloadLimit, int uploadLimit)
    {
        if (_torrentLimiters.TryGetValue(infoHash, out var limiter))
        {
            limiter.SetDownloadLimit(downloadLimit);
            limiter.SetUploadLimit(uploadLimit);
        }
    }

    /// <summary>
    /// Gets aggregated bandwidth statistics.
    /// </summary>
    public GlobalBandwidthStats GetStats()
    {
        return new GlobalBandwidthStats
        {
            DownloadStats = _downloadManager.GetStats(),
            UploadStats = _uploadManager.GetStats(),
            TorrentLimiterCount = _torrentLimiters.Count
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        foreach (var limiter in _torrentLimiters.Values)
        {
            limiter.Dispose();
        }
        _torrentLimiters.Clear();

        _downloadManager.Dispose();
        _uploadManager.Dispose();
    }
}

/// <summary>
/// Aggregated bandwidth statistics.
/// </summary>
public class GlobalBandwidthStats
{
    public BandwidthStats DownloadStats { get; init; } = new();
    public BandwidthStats UploadStats { get; init; } = new();
    public int TorrentLimiterCount { get; init; }

    public override string ToString()
    {
        return $"GlobalBandwidthStats[\n  Download: {DownloadStats}\n  Upload: {UploadStats}\n  Torrents: {TorrentLimiterCount}]";
    }
}
