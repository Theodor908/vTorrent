using System;
using System.Threading;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Distributes bandwidth across torrents using token bucket algorithm.
/// Based on libtorrent's bandwidth_manager.
/// </summary>
public class BandwidthAllocator : IDisposable
{
    private readonly object _lock = new();
    private readonly Timer _distributeTimer;
    private bool _disposed;

    // Global limits (bytes/sec, 0 = unlimited)
    private int _globalDownloadLimit;
    private int _globalUploadLimit;

    // Per-torrent default limits (bytes/sec, 0 = unlimited)
    private int _defaultPerTorrentDownloadLimit;
    private int _defaultPerTorrentUploadLimit;

    // Token buckets
    private long _downloadTokens;
    private long _uploadTokens;
    private DateTime _lastUpdate = DateTime.UtcNow;

    // Maximum burst size (3 seconds worth of bandwidth, matching libtorrent)
    // This allows short bursts while maintaining long-term rate limits
    private const int MaxBurstMultiplier = 3;

    // Update interval in milliseconds
    private const int UpdateIntervalMs = 100;

    public BandwidthAllocator()
    {
        // Update quotas every 100ms for smooth distribution
        _distributeTimer = new Timer(
            UpdateQuotas,
            null,
            TimeSpan.FromMilliseconds(UpdateIntervalMs),
            TimeSpan.FromMilliseconds(UpdateIntervalMs));
    }

    /// <summary>
    /// Global download limit in bytes/sec (0 = unlimited)
    /// </summary>
    public int GlobalDownloadLimit
    {
        get => _globalDownloadLimit;
        set
        {
            lock (_lock)
            {
                _globalDownloadLimit = value;
                // Reset tokens when limit changes
                if (value > 0)
                    _downloadTokens = value * MaxBurstMultiplier;
            }
        }
    }

    /// <summary>
    /// Global upload limit in bytes/sec (0 = unlimited)
    /// </summary>
    public int GlobalUploadLimit
    {
        get => _globalUploadLimit;
        set
        {
            lock (_lock)
            {
                _globalUploadLimit = value;
                // Reset tokens when limit changes
                if (value > 0)
                    _uploadTokens = value * MaxBurstMultiplier;
            }
        }
    }

    /// <summary>
    /// Default per-torrent download limit in bytes/sec (0 = unlimited)
    /// Applied to new torrents and used as fallback for torrents without specific limits
    /// </summary>
    public int DefaultPerTorrentDownloadLimit
    {
        get => _defaultPerTorrentDownloadLimit;
        set
        {
            lock (_lock)
            {
                _defaultPerTorrentDownloadLimit = value;
            }
        }
    }

    /// <summary>
    /// Default per-torrent upload limit in bytes/sec (0 = unlimited)
    /// Applied to new torrents and used as fallback for torrents without specific limits
    /// </summary>
    public int DefaultPerTorrentUploadLimit
    {
        get => _defaultPerTorrentUploadLimit;
        set
        {
            lock (_lock)
            {
                _defaultPerTorrentUploadLimit = value;
            }
        }
    }

    /// <summary>
    /// Get the effective download limit for a torrent.
    /// Returns the torrent-specific limit if set, otherwise the default per-torrent limit,
    /// capped by the global limit if set.
    /// </summary>
    public int GetEffectiveDownloadLimit(int torrentSpecificLimit)
    {
        lock (_lock)
        {
            // If torrent has specific limit, use it
            int limit = torrentSpecificLimit > 0 ? torrentSpecificLimit : _defaultPerTorrentDownloadLimit;

            // Cap by global limit if set
            if (_globalDownloadLimit > 0 && (limit == 0 || limit > _globalDownloadLimit))
                limit = _globalDownloadLimit;

            return limit;
        }
    }

    /// <summary>
    /// Get the effective upload limit for a torrent.
    /// Returns the torrent-specific limit if set, otherwise the default per-torrent limit,
    /// capped by the global limit if set.
    /// </summary>
    public int GetEffectiveUploadLimit(int torrentSpecificLimit)
    {
        lock (_lock)
        {
            // If torrent has specific limit, use it
            int limit = torrentSpecificLimit > 0 ? torrentSpecificLimit : _defaultPerTorrentUploadLimit;

            // Cap by global limit if set
            if (_globalUploadLimit > 0 && (limit == 0 || limit > _globalUploadLimit))
                limit = _globalUploadLimit;

            return limit;
        }
    }

    /// <summary>
    /// Check if unlimited bandwidth is available
    /// </summary>
    public bool IsUnlimited(BandwidthChannel channel)
    {
        return channel == BandwidthChannel.Download
            ? _globalDownloadLimit == 0
            : _globalUploadLimit == 0;
    }

    /// <summary>
    /// Request bandwidth quota (returns immediately available amount)
    /// Fast path: If unlimited, return requested amount immediately
    /// </summary>
    /// <param name="channel">Download or upload</param>
    /// <param name="amount">Requested amount in bytes</param>
    /// <param name="priority">Higher priority gets preference (not currently used)</param>
    /// <returns>Amount of bandwidth granted (may be less than requested)</returns>
    public int RequestQuota(BandwidthChannel channel, int amount, int priority = 0)
    {
        // Fast path for unlimited
        if (IsUnlimited(channel))
            return amount;

        lock (_lock)
        {
            ref long tokens = ref (channel == BandwidthChannel.Download
                ? ref _downloadTokens
                : ref _uploadTokens);

            if (tokens <= 0)
                return 0; // No quota available

            // Grant up to requested amount
            int granted = (int)Math.Min(amount, tokens);
            tokens -= granted;

            return granted;
        }
    }

    /// <summary>
    /// Return unused quota (e.g., if send/receive was partial)
    /// </summary>
    public void ReturnQuota(BandwidthChannel channel, int amount)
    {
        if (IsUnlimited(channel) || amount <= 0)
            return;

        lock (_lock)
        {
            ref long tokens = ref (channel == BandwidthChannel.Download
                ? ref _downloadTokens
                : ref _uploadTokens);

            int limit = channel == BandwidthChannel.Download
                ? _globalDownloadLimit
                : _globalUploadLimit;

            // Don't exceed max burst
            long maxTokens = limit * MaxBurstMultiplier;
            tokens = Math.Min(maxTokens, tokens + amount);
        }
    }

    /// <summary>
    /// Check available quota without consuming
    /// </summary>
    public int GetAvailableQuota(BandwidthChannel channel)
    {
        if (IsUnlimited(channel))
            return int.MaxValue;

        lock (_lock)
        {
            return channel == BandwidthChannel.Download
                ? (int)Math.Max(0, _downloadTokens)
                : (int)Math.Max(0, _uploadTokens);
        }
    }

    /// <summary>
    /// Update token buckets based on elapsed time
    /// </summary>
    private void UpdateQuotas(object? state)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastUpdate).TotalSeconds;
            _lastUpdate = now;

            if (_globalDownloadLimit > 0)
            {
                long maxTokens = (long)_globalDownloadLimit * MaxBurstMultiplier;
                long toAdd = (long)(_globalDownloadLimit * elapsed);
                _downloadTokens = Math.Min(maxTokens, _downloadTokens + toAdd);
            }

            if (_globalUploadLimit > 0)
            {
                long maxTokens = (long)_globalUploadLimit * MaxBurstMultiplier;
                long toAdd = (long)(_globalUploadLimit * elapsed);
                _uploadTokens = Math.Min(maxTokens, _uploadTokens + toAdd);
            }
        }
    }

    /// <summary>
    /// Get statistics snapshot
    /// </summary>
    public BandwidthStats GetStats()
    {
        lock (_lock)
        {
            return new BandwidthStats
            {
                DownloadLimit = _globalDownloadLimit,
                UploadLimit = _globalUploadLimit,
                AvailableDownloadQuota = (int)Math.Max(0, _downloadTokens),
                AvailableUploadQuota = (int)Math.Max(0, _uploadTokens)
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _distributeTimer.Dispose();
    }
}

/// <summary>
/// Bandwidth channel (direction)
/// </summary>
public enum BandwidthChannel
{
    Download,
    Upload
}

/// <summary>
/// Bandwidth statistics snapshot
/// </summary>
public readonly struct BandwidthStats
{
    public int DownloadLimit { get; init; }
    public int UploadLimit { get; init; }
    public int AvailableDownloadQuota { get; init; }
    public int AvailableUploadQuota { get; init; }
    public bool IsDownloadLimited => DownloadLimit > 0;
    public bool IsUploadLimited => UploadLimit > 0;
}
