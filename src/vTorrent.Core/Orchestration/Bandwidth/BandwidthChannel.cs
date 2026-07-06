using System;
using System.Threading;

namespace vTorrent.Core.Orchestration.Bandwidth;

/// <summary>
/// Represents a single bandwidth rate-limit channel using a leaky bucket algorithm.
/// Based on libtorrent's bandwidth_channel implementation.
///
/// The bucket accumulates quota over time at the rate limit, capped at 3x the per-second limit
/// to allow short bursts while maintaining long-term rate limits.
/// </summary>
public class BandwidthChannel
{
    private readonly object _lock = new();

    /// <summary>
    /// Accumulated unused quota (bytes). Can be negative if overconsumed.
    /// </summary>
    private long _quotaLeft;

    /// <summary>
    /// Rate limit in bytes per second. 0 = unlimited.
    /// </summary>
    private int _limit;

    /// <summary>
    /// Quota available for distribution this round.
    /// Updated by UpdateQuota(), consumed by requests.
    /// </summary>
    public int DistributeQuota { get; private set; }

    /// <summary>
    /// Temporary storage for sum of priorities during distribution.
    /// Used by BandwidthManager to calculate fair shares.
    /// </summary>
    public int TotalPriority { get; set; }

    /// <summary>
    /// Name identifier for debugging.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Creates a new bandwidth channel.
    /// </summary>
    /// <param name="name">Identifier for this channel (e.g., "global_download", "torrent_upload")</param>
    /// <param name="limitBytesPerSecond">Rate limit in bytes/sec. 0 = unlimited.</param>
    public BandwidthChannel(string name, int limitBytesPerSecond = 0)
    {
        Name = name ?? "unnamed";
        _limit = Math.Max(0, limitBytesPerSecond);
        _quotaLeft = _limit > 0 ? _limit : 0; // Start with 1 second of quota
    }

    /// <summary>
    /// Gets or sets the rate limit in bytes per second.
    /// 0 = unlimited (no rate limiting).
    /// </summary>
    public int Throttle
    {
        get { lock (_lock) return _limit; }
        set
        {
            lock (_lock)
            {
                _limit = Math.Max(0, value);
                // Reset quota when limit changes
                if (_limit > 0)
                {
                    _quotaLeft = _limit; // 1 second of quota
                }
            }
        }
    }

    /// <summary>
    /// Gets the current available quota in bytes.
    /// </summary>
    public long QuotaLeft
    {
        get { lock (_lock) return Math.Max(0, _quotaLeft); }
    }

    /// <summary>
    /// Whether this channel is unlimited (no rate limiting).
    /// </summary>
    public bool IsUnlimited
    {
        get { lock (_lock) return _limit == 0; }
    }

    /// <summary>
    /// Updates the quota based on elapsed time (leaky bucket accumulation).
    /// Called periodically by BandwidthManager.
    /// </summary>
    /// <param name="elapsedMilliseconds">Time since last update in milliseconds</param>
    public void UpdateQuota(int elapsedMilliseconds)
    {
        lock (_lock)
        {
            if (_limit == 0)
            {
                // Unlimited - no quota tracking needed
                DistributeQuota = int.MaxValue;
                return;
            }

            // Cap elapsed time to 3 seconds to prevent huge bursts after long pauses
            elapsedMilliseconds = Math.Min(elapsedMilliseconds, 3000);

            // Calculate bytes to add: limit * elapsed_time / 1000
            // Using long to avoid overflow
            long toAdd = ((long)_limit * elapsedMilliseconds + 500) / 1000;

            _quotaLeft += toAdd;

            // Cap quota at 3x the per-second limit (prevents excessive bursts)
            long maxQuota = (long)_limit * 3;
            if (_quotaLeft > maxQuota)
            {
                _quotaLeft = maxQuota;
            }

            // Convert to distributable amount (never negative)
            DistributeQuota = (int)Math.Min(Math.Max(_quotaLeft, 0), int.MaxValue);
        }
    }

    /// <summary>
    /// Checks if a request for the given amount needs to be queued.
    /// IMPORTANT: This method does NOT consume quota - call ConsumeQuota() separately after
    /// all channels have been checked. This follows libtorrent's pattern and prevents
    /// quota loss when multi-channel requests fail on a later channel.
    /// </summary>
    /// <param name="amount">Bytes requested</param>
    /// <returns>True if request needs to be queued, false if can be granted immediately</returns>
    public bool NeedsQueueing(int amount)
    {
        lock (_lock)
        {
            if (_limit == 0)
            {
                // Unlimited - never needs queueing
                return false;
            }

            // Fast-path any request that fits in remaining quota; queue only when quota is exhausted.
            return _quotaLeft - amount < 0;
        }
    }

    /// <summary>
    /// Consumes quota from this channel for a fast-path grant.
    /// Call this ONLY after verifying all channels can grant via NeedsQueueing().
    /// This two-phase approach prevents quota loss in multi-channel scenarios.
    /// </summary>
    /// <param name="amount">Bytes to consume</param>
    public void ConsumeQuotaForFastPath(int amount)
    {
        lock (_lock)
        {
            if (_limit == 0) return; // Unlimited - no quota tracking

            _quotaLeft -= amount;
            DistributeQuota = (int)Math.Min(Math.Max(_quotaLeft, 0), int.MaxValue);
        }
    }

    /// <summary>
    /// Consumes quota from this channel.
    /// Called when bandwidth is assigned to a request.
    /// </summary>
    /// <param name="amount">Bytes consumed</param>
    public void UseQuota(int amount)
    {
        lock (_lock)
        {
            if (_limit == 0) return; // Unlimited

            _quotaLeft -= amount;
            DistributeQuota = (int)Math.Min(Math.Max(_quotaLeft, 0), int.MaxValue);
        }
    }

    /// <summary>
    /// Returns unused quota back to the channel.
    /// Called when a peer disconnects with unused assigned quota.
    /// </summary>
    /// <param name="amount">Bytes to return</param>
    public void ReturnQuota(int amount)
    {
        lock (_lock)
        {
            if (_limit == 0) return; // Unlimited

            _quotaLeft += amount;

            // Cap at 3x limit
            long maxQuota = (long)_limit * 3;
            if (_quotaLeft > maxQuota)
            {
                _quotaLeft = maxQuota;
            }
        }
    }

    /// <summary>
    /// Resets the channel state (for testing or reinitialization).
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _quotaLeft = _limit > 0 ? _limit : 0;
            DistributeQuota = 0;
            TotalPriority = 0;
        }
    }

    public override string ToString()
    {
        return $"BandwidthChannel[{Name}, limit={_limit}B/s, quota={_quotaLeft}B]";
    }
}
