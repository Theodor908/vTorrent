using System;
using System.Collections.Concurrent;
using System.Threading;

namespace vTorrent.Core.PeerCommunication.Bandwidth;

/// <summary>
/// Adapter that provides bandwidth limiting for peer connections.
/// Wraps the core bandwidth management system with a peer-friendly interface.
/// Following libtorrent's pattern of per-connection quota tracking.
/// </summary>
public class PeerBandwidthLimiterAdapter : IPeerBandwidthLimiter, IDisposable
{
    private readonly Func<int, int, int> _requestDownload;  // (priority, bytes) => granted
    private readonly Func<int, int, int> _requestUpload;    // (priority, bytes) => granted
    private readonly Action<object>? _cancelRequests;

    private readonly int _downloadLimit;
    private readonly int _uploadLimit;

    // Per-consumer quota tracking
    private readonly ConcurrentDictionary<string, ConsumerQuotaState> _consumerStates = new();

    public bool IsDownloadLimited => _downloadLimit > 0;
    public bool IsUploadLimited => _uploadLimit > 0;
    public int EffectiveDownloadLimit => _downloadLimit;
    public int EffectiveUploadLimit => _uploadLimit;

    /// <summary>
    /// Creates a bandwidth limiter adapter with specified limits.
    /// </summary>
    /// <param name="downloadLimit">Download limit in bytes/sec (0 = unlimited)</param>
    /// <param name="uploadLimit">Upload limit in bytes/sec (0 = unlimited)</param>
    /// <param name="requestDownload">Function to request download quota from core system</param>
    /// <param name="requestUpload">Function to request upload quota from core system</param>
    /// <param name="cancelRequests">Action to cancel requests for a consumer</param>
    public PeerBandwidthLimiterAdapter(
        int downloadLimit,
        int uploadLimit,
        Func<int, int, int>? requestDownload = null,
        Func<int, int, int>? requestUpload = null,
        Action<object>? cancelRequests = null)
    {
        _downloadLimit = Math.Max(0, downloadLimit);
        _uploadLimit = Math.Max(0, uploadLimit);
        _requestDownload = requestDownload ?? ((_, bytes) => bytes);
        _requestUpload = requestUpload ?? ((_, bytes) => bytes);
        _cancelRequests = cancelRequests;
    }

    public int RequestDownloadQuota(IPeerBandwidthConsumer consumer, int bytes)
    {
        if (consumer == null || bytes <= 0) return 0;
        if (!IsDownloadLimited) return bytes; // Unlimited

        var state = GetOrCreateState(consumer);

        // Check if we have quota available
        if (state.DownloadQuota >= bytes)
        {
            state.DownloadQuota -= bytes;
            return bytes;
        }

        // Request more quota from the core system
        int requested = Math.Max(bytes, _downloadLimit / 10); // Request at least 100ms worth
        int granted = _requestDownload(consumer.BandwidthPriority, requested);

        state.DownloadQuota += granted;

        // Return what we can
        int available = Math.Min(state.DownloadQuota, bytes);
        state.DownloadQuota -= available;
        return available;
    }

    public int RequestUploadQuota(IPeerBandwidthConsumer consumer, int bytes)
    {
        if (consumer == null || bytes <= 0) return 0;
        if (!IsUploadLimited) return bytes; // Unlimited

        var state = GetOrCreateState(consumer);

        // Check if we have quota available
        if (state.UploadQuota >= bytes)
        {
            state.UploadQuota -= bytes;
            return bytes;
        }

        // Request more quota from the core system
        int requested = Math.Max(bytes, _uploadLimit / 10); // Request at least 100ms worth
        int granted = _requestUpload(consumer.BandwidthPriority, requested);

        state.UploadQuota += granted;

        // Return what we can
        int available = Math.Min(state.UploadQuota, bytes);
        state.UploadQuota -= available;
        return available;
    }

    public void CancelRequests(IPeerBandwidthConsumer consumer)
    {
        if (consumer == null) return;

        _consumerStates.TryRemove(consumer.ConsumerId, out _);
        _cancelRequests?.Invoke(consumer);
    }

    /// <summary>
    /// Adds quota to a consumer (called by bandwidth manager on tick).
    /// </summary>
    public void AddDownloadQuota(string consumerId, int bytes)
    {
        if (_consumerStates.TryGetValue(consumerId, out var state))
        {
            Interlocked.Add(ref state.DownloadQuota, bytes);
            state.Consumer?.OnDownloadQuotaAssigned(bytes);
        }
    }

    /// <summary>
    /// Adds upload quota to a consumer.
    /// </summary>
    public void AddUploadQuota(string consumerId, int bytes)
    {
        if (_consumerStates.TryGetValue(consumerId, out var state))
        {
            Interlocked.Add(ref state.UploadQuota, bytes);
            state.Consumer?.OnUploadQuotaAssigned(bytes);
        }
    }

    private ConsumerQuotaState GetOrCreateState(IPeerBandwidthConsumer consumer)
    {
        return _consumerStates.GetOrAdd(consumer.ConsumerId, _ => new ConsumerQuotaState
        {
            Consumer = consumer,
            DownloadQuota = 0,
            UploadQuota = 0
        });
    }

    public void Dispose()
    {
        _consumerStates.Clear();
    }

    private class ConsumerQuotaState
    {
        public IPeerBandwidthConsumer? Consumer;
        public int DownloadQuota;
        public int UploadQuota;
    }
}

/// <summary>
/// Simple token bucket rate limiter for standalone use.
/// Provides rate limiting without external coordination.
/// Based on libtorrent's bandwidth_channel.
/// </summary>
public class SimpleRateLimiter : IPeerBandwidthLimiter, IDisposable
{
    private readonly object _lock = new();
    private readonly int _downloadLimit;
    private readonly int _uploadLimit;

    private long _downloadTokens;
    private long _uploadTokens;
    private DateTime _lastUpdate;

    // Maximum burst = 3 seconds of quota (libtorrent default)
    private const int MaxBurstMultiplier = 3;

    public bool IsDownloadLimited => _downloadLimit > 0;
    public bool IsUploadLimited => _uploadLimit > 0;
    public int EffectiveDownloadLimit => _downloadLimit;
    public int EffectiveUploadLimit => _uploadLimit;

    public SimpleRateLimiter(int downloadLimitBytesPerSec, int uploadLimitBytesPerSec)
    {
        _downloadLimit = Math.Max(0, downloadLimitBytesPerSec);
        _uploadLimit = Math.Max(0, uploadLimitBytesPerSec);

        // Start with 1 second of quota
        _downloadTokens = _downloadLimit;
        _uploadTokens = _uploadLimit;
        _lastUpdate = DateTime.UtcNow;
    }

    public int RequestDownloadQuota(IPeerBandwidthConsumer consumer, int bytes)
    {
        if (bytes <= 0) return 0;
        if (_downloadLimit == 0) return bytes; // Unlimited

        lock (_lock)
        {
            ReplenishTokens();

            int available = (int)Math.Min(_downloadTokens, bytes);
            _downloadTokens -= available;
            return available;
        }
    }

    public int RequestUploadQuota(IPeerBandwidthConsumer consumer, int bytes)
    {
        if (bytes <= 0) return 0;
        if (_uploadLimit == 0) return bytes; // Unlimited

        lock (_lock)
        {
            ReplenishTokens();

            int available = (int)Math.Min(_uploadTokens, bytes);
            _uploadTokens -= available;
            return available;
        }
    }

    public void CancelRequests(IPeerBandwidthConsumer consumer)
    {
        // Simple rate limiter doesn't track per-consumer state
    }

    private void ReplenishTokens()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastUpdate;
        _lastUpdate = now;

        // Cap elapsed time to prevent huge bursts after long pauses
        var elapsedMs = Math.Min(elapsed.TotalMilliseconds, 3000);

        if (_downloadLimit > 0)
        {
            long toAdd = (long)(_downloadLimit * elapsedMs / 1000.0);
            _downloadTokens = Math.Min(_downloadTokens + toAdd, (long)_downloadLimit * MaxBurstMultiplier);
        }

        if (_uploadLimit > 0)
        {
            long toAdd = (long)(_uploadLimit * elapsedMs / 1000.0);
            _uploadTokens = Math.Min(_uploadTokens + toAdd, (long)_uploadLimit * MaxBurstMultiplier);
        }
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
