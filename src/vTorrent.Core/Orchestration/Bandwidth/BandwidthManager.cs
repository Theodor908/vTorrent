using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace vTorrent.Core.Orchestration.Bandwidth;

/// <summary>
/// Central bandwidth distribution manager.
/// Based on libtorrent's bandwidth_manager implementation.
///
/// Manages a queue of bandwidth requests and distributes available quota
/// using priority-weighted fair queuing with starvation prevention.
/// </summary>
public class BandwidthManager : IDisposable
{
    private readonly ILogger<BandwidthManager> _logger;
    private readonly object _lock = new();
    private readonly List<BandwidthRequest> _queue = new();
    private readonly BandwidthChannelType _channelType;
    private readonly Stopwatch _stopwatch = new();

    private long _queuedBytes;
    private bool _disposed;
    private long _lastUpdateMs;

    /// <summary>
    /// Global bandwidth channel for this manager.
    /// </summary>
    public BandwidthChannel GlobalChannel { get; }

    /// <summary>
    /// Per-torrent default channel (optional, used when torrent doesn't have specific limit).
    /// </summary>
    public BandwidthChannel? DefaultPerTorrentChannel { get; private set; }

    /// <summary>
    /// Gets the channel type (download or upload).
    /// </summary>
    public BandwidthChannelType ChannelType => _channelType;

    /// <summary>
    /// Gets the number of pending requests in the queue.
    /// </summary>
    public int QueueLength
    {
        get { lock (_lock) return _queue.Count; }
    }

    /// <summary>
    /// Gets the total bytes queued for distribution.
    /// </summary>
    public long QueuedBytes
    {
        get { lock (_lock) return _queuedBytes; }
    }

    /// <summary>
    /// Creates a new bandwidth manager.
    /// </summary>
    /// <param name="channelType">Download or upload</param>
    /// <param name="globalLimitBytesPerSecond">Global rate limit (0 = unlimited)</param>
    /// <param name="logger">Logger instance</param>
    public BandwidthManager(BandwidthChannelType channelType, int globalLimitBytesPerSecond, ILogger<BandwidthManager> logger)
    {
        _channelType = channelType;
        _logger = logger;

        string channelName = channelType == BandwidthChannelType.Download ? "global_download" : "global_upload";
        GlobalChannel = new BandwidthChannel(channelName, globalLimitBytesPerSecond);

        _stopwatch.Start();
        _lastUpdateMs = _stopwatch.ElapsedMilliseconds;

        _logger.LogDebug("BandwidthManager created for {Channel} with global limit {Limit} B/s",
            channelType, globalLimitBytesPerSecond);
    }

    /// <summary>
    /// Sets the global rate limit.
    /// </summary>
    /// <param name="bytesPerSecond">Rate limit in bytes/sec (0 = unlimited)</param>
    public void SetGlobalLimit(int bytesPerSecond)
    {
        GlobalChannel.Throttle = bytesPerSecond;
        _logger.LogDebug("Global {Channel} limit set to {Limit} B/s", _channelType, bytesPerSecond);
    }

    /// <summary>
    /// Sets the default per-torrent rate limit.
    /// </summary>
    /// <param name="bytesPerSecond">Rate limit in bytes/sec (0 = unlimited/use global)</param>
    public void SetDefaultPerTorrentLimit(int bytesPerSecond)
    {
        if (bytesPerSecond > 0)
        {
            string name = _channelType == BandwidthChannelType.Download
                ? "default_torrent_download"
                : "default_torrent_upload";

            DefaultPerTorrentChannel ??= new BandwidthChannel(name, bytesPerSecond);
            DefaultPerTorrentChannel.Throttle = bytesPerSecond;
        }
        else
        {
            DefaultPerTorrentChannel = null;
        }
    }

    /// <summary>
    /// Requests bandwidth for a consumer.
    /// If quota is immediately available (fast path), returns the requested amount.
    /// Otherwise queues the request and returns 0.
    /// </summary>
    /// <param name="consumer">The bandwidth consumer</param>
    /// <param name="requestSize">Bytes requested</param>
    /// <param name="priority">Priority (1-255, default 128)</param>
    /// <param name="additionalChannels">Additional limiting channels (e.g., per-torrent limits)</param>
    /// <returns>Bytes granted immediately (0 if queued)</returns>
    public int RequestBandwidth(
        IBandwidthConsumer consumer,
        int requestSize,
        int priority = 128,
        params BandwidthChannel?[] additionalChannels)
    {
        if (consumer == null) return requestSize;
        if (requestSize <= 0) return 0;
        if (consumer.IsDisconnecting) return 0;

        lock (_lock)
        {
            // Collect all limiting channels
            var channels = new List<BandwidthChannel>();

            // Add global channel if limited
            if (!GlobalChannel.IsUnlimited)
            {
                channels.Add(GlobalChannel);
            }

            // Add default per-torrent channel if set
            if (DefaultPerTorrentChannel != null && !DefaultPerTorrentChannel.IsUnlimited)
            {
                channels.Add(DefaultPerTorrentChannel);
            }

            // Add any additional channels
            foreach (var ch in additionalChannels)
            {
                if (ch != null && !ch.IsUnlimited)
                {
                    channels.Add(ch);
                }
            }

            // Fast path: if no channels limit this, grant immediately
            if (channels.Count == 0)
            {
                return requestSize;
            }

            // TWO-PHASE CHECK (following libtorrent pattern):
            // Phase 1: Check ALL channels first WITHOUT consuming quota
            // This prevents quota loss when multi-channel requests fail on a later channel
            bool needsQueueing = false;
            foreach (var ch in channels)
            {
                if (ch.NeedsQueueing(requestSize))
                {
                    needsQueueing = true;
                    break;
                }
            }

            // Fast path: all channels have enough quota
            if (!needsQueueing)
            {
                // Phase 2: NOW consume quota from all channels (all passed the check)
                foreach (var ch in channels)
                {
                    ch.ConsumeQuotaForFastPath(requestSize);
                }
                return requestSize;
            }

            // Slow path: queue the request
            var request = new BandwidthRequest(consumer, requestSize, priority);
            foreach (var ch in channels)
            {
                request.AddChannel(ch);
            }

            _queue.Add(request);
            _queuedBytes += requestSize;

            return 0; // Queued, no immediate grant
        }
    }

    /// <summary>
    /// Updates quotas and distributes bandwidth to queued requests.
    /// Should be called periodically (e.g., every 100ms).
    /// </summary>
    public void UpdateQuotas()
    {
        lock (_lock)
        {
            if (_queue.Count == 0)
            {
                // Still update channel quotas to accumulate
                long currentMs = _stopwatch.ElapsedMilliseconds;
                int elapsedMs = (int)Math.Min(currentMs - _lastUpdateMs, 3000);
                _lastUpdateMs = currentMs;
                GlobalChannel.UpdateQuota(elapsedMs);
                DefaultPerTorrentChannel?.UpdateQuota(elapsedMs);
                return;
            }

            // Calculate elapsed time
            long now = _stopwatch.ElapsedMilliseconds;
            int elapsed = (int)Math.Min(now - _lastUpdateMs, 3000);
            _lastUpdateMs = now;

            // Step 1: Remove disconnected consumers, return their quota
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                var request = _queue[i];
                if (request.Consumer.IsDisconnecting)
                {
                    request.ReturnQuota();
                    _queuedBytes -= request.RequestSize;
                    _queue.RemoveAt(i);
                }
            }

            if (_queue.Count == 0) return;

            // Step 2: Collect unique channels and sum priorities
            var channelsInUse = new HashSet<BandwidthChannel>();
            foreach (var request in _queue)
            {
                for (int j = 0; j < request.ChannelCount; j++)
                {
                    var ch = request.Channels[j];
                    if (ch != null)
                    {
                        if (channelsInUse.Add(ch))
                        {
                            ch.TotalPriority = 0; // Reset for this round
                        }
                        ch.TotalPriority += request.Priority;
                    }
                }
            }

            // Step 3: Update quota in each channel (leaky bucket accumulation)
            foreach (var ch in channelsInUse)
            {
                ch.UpdateQuota(elapsed);
            }

            // Also update channels not in use (to accumulate quota)
            if (!channelsInUse.Contains(GlobalChannel))
            {
                GlobalChannel.UpdateQuota(elapsed);
            }
            if (DefaultPerTorrentChannel != null && !channelsInUse.Contains(DefaultPerTorrentChannel))
            {
                DefaultPerTorrentChannel.UpdateQuota(elapsed);
            }

            // Step 4: Assign bandwidth to requests
            var completedRequests = new List<BandwidthRequest>();

            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                var request = _queue[i];
                int assigned = request.AssignBandwidth();

                // Check if fully satisfied or TTL expired (starvation prevention)
                if (request.IsSatisfied || (request.Ttl <= 0 && request.Assigned > 0))
                {
                    completedRequests.Add(request);
                    _queuedBytes -= request.RequestSize;
                    _queue.RemoveAt(i);
                }
            }

            // Step 5: Callback to consumers to deliver bandwidth
            foreach (var request in completedRequests)
            {
                try
                {
                    request.Consumer.OnBandwidthAssigned(_channelType, request.Assigned);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error notifying consumer {Id} of bandwidth assignment",
                        request.Consumer.Id);
                }
            }
        }
    }

    /// <summary>
    /// Cancels all pending requests for a consumer.
    /// Called when a consumer disconnects.
    /// </summary>
    /// <param name="consumer">The consumer to remove</param>
    public void CancelRequests(IBandwidthConsumer consumer)
    {
        if (consumer == null) return;

        lock (_lock)
        {
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                var request = _queue[i];
                if (ReferenceEquals(request.Consumer, consumer))
                {
                    request.ReturnQuota();
                    _queuedBytes -= request.RequestSize;
                    _queue.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// Gets statistics about the current state.
    /// </summary>
    public BandwidthStats GetStats()
    {
        lock (_lock)
        {
            return new BandwidthStats
            {
                ChannelType = _channelType,
                GlobalLimit = GlobalChannel.Throttle,
                GlobalQuotaLeft = GlobalChannel.QuotaLeft,
                PerTorrentLimit = DefaultPerTorrentChannel?.Throttle ?? 0,
                PerTorrentQuotaLeft = DefaultPerTorrentChannel?.QuotaLeft ?? 0,
                QueueLength = _queue.Count,
                QueuedBytes = _queuedBytes
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            // Return all quota
            foreach (var request in _queue)
            {
                request.ReturnQuota();
            }
            _queue.Clear();
            _queuedBytes = 0;
        }

        _stopwatch.Stop();
    }
}

/// <summary>
/// Statistics about bandwidth manager state.
/// </summary>
public class BandwidthStats
{
    public BandwidthChannelType ChannelType { get; init; }
    public int GlobalLimit { get; init; }
    public long GlobalQuotaLeft { get; init; }
    public int PerTorrentLimit { get; init; }
    public long PerTorrentQuotaLeft { get; init; }
    public int QueueLength { get; init; }
    public long QueuedBytes { get; init; }

    public override string ToString()
    {
        return $"BandwidthStats[{ChannelType}, global={GlobalLimit}B/s ({GlobalQuotaLeft}B left), " +
               $"perTorrent={PerTorrentLimit}B/s ({PerTorrentQuotaLeft}B left), queue={QueueLength} ({QueuedBytes}B)]";
    }
}
