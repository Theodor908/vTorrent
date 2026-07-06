using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.DHT
{
    /// <summary>
    /// Rate-limiting DoS protection matching libtorrent's dos_blocker implementation.
    /// Tracks incoming packet rates per IP and temporarily bans abusive IPs.
    /// </summary>
    public class DosBlocker
    {
        private readonly IOptionsMonitor<DhtSettings> _dhtMonitor;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<IPAddress, RateLimitEntry> _entries;
        private readonly LinkedList<IPAddress> _lruOrder;
        private readonly object _lock = new();

        public DosBlocker(IOptionsMonitor<DhtSettings> dhtMonitor, ILogger logger = null)
        {
            _dhtMonitor = dhtMonitor ?? throw new ArgumentNullException(nameof(dhtMonitor));
            _logger = logger;
            _entries = new ConcurrentDictionary<IPAddress, RateLimitEntry>();
            _lruOrder = new LinkedList<IPAddress>();
        }

        /// <summary>
        /// Gets the number of currently tracked IPs.
        /// </summary>
        public int TrackedIpCount => _entries.Count;

        /// <summary>
        /// Gets the number of currently blocked IPs.
        /// </summary>
        public int BlockedIpCount
        {
            get
            {
                int count = 0;
                var now = DateTime.UtcNow;
                foreach (var entry in _entries.Values)
                {
                    if (entry.BlockedUntil > now)
                        count++;
                }
                return count;
            }
        }

        /// <summary>
        /// Checks if an IP is currently blocked.
        /// </summary>
        /// <param name="ip">The IP address to check.</param>
        /// <returns>True if the IP is blocked, false otherwise.</returns>
        public bool IsBlocked(IPAddress ip)
        {
            if (_entries.TryGetValue(ip, out var entry))
            {
                return entry.BlockedUntil > DateTime.UtcNow;
            }
            return false;
        }

        /// <summary>
        /// Records an incoming packet from an IP address.
        /// Returns true if the packet should be processed, false if rate-limited.
        /// This matches libtorrent's dos_blocker::incoming() behavior.
        /// </summary>
        /// <param name="ip">The source IP address.</param>
        /// <returns>True to allow the packet, false to drop it.</returns>
        public bool RecordPacket(IPAddress ip)
        {
            var settings = _dhtMonitor.CurrentValue;

            if (!settings.EnableDosBlocker)
                return true;

            if (settings.BlockRateLimitPacketsPerSec <= 0)
                return true;

            var now = DateTime.UtcNow;

            var entry = _entries.GetOrAdd(ip, _ => new RateLimitEntry());

            lock (_lock)
            {
                if (entry.LruNode != null)
                    _lruOrder.Remove(entry.LruNode);
                entry.LruNode = _lruOrder.AddLast(ip);

                while (_lruOrder.Count > settings.MaxBlockedIps && _lruOrder.First != null)
                {
                    var oldest = _lruOrder.First.Value;
                    _lruOrder.RemoveFirst();
                    if (_entries.TryRemove(oldest, out var removedEntry))
                        removedEntry.LruNode = null;
                }
            }

            // Check if currently blocked
            if (entry.BlockedUntil > now)
            {
                _logger?.LogDebug("Blocked packet from {IP} (blocked until {Until})", ip, entry.BlockedUntil);
                return false;
            }

            // Count-in-window algorithm (libtorrent dos_blocker parity)
            // Threshold: BlockRateLimitPacketsPerSec * 10 messages per 10-second window
            int threshold = settings.BlockRateLimitPacketsPerSec * 10;

            // Check if window expired — reset if so
            if (now >= entry.WindowExpiry)
            {
                entry.PacketCount = 0;
                entry.WindowExpiry = now.AddSeconds(10);
            }

            entry.PacketCount++;

            if (entry.PacketCount >= threshold)
            {
                // Hit threshold within window → BAN
                entry.BlockedUntil = now.AddSeconds(settings.BlockTimeoutSeconds);
                _logger?.LogWarning(
                    "Rate limit exceeded for {IP} ({Count} messages in 10s window >= {Limit}), blocking for {Seconds}s",
                    ip, entry.PacketCount, threshold, settings.BlockTimeoutSeconds);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Clears all rate limiting state.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _lruOrder.Clear();
            }
        }

        /// <summary>
        /// Removes expired blocks and stale entries.
        /// </summary>
        public void Cleanup()
        {
            var now = DateTime.UtcNow;
            var toRemove = new List<IPAddress>();

            foreach (var kvp in _entries)
            {
                var entry = kvp.Value;
                // Remove entries with expired windows that are not blocked
                if (entry.BlockedUntil <= now && entry.WindowExpiry < now.AddSeconds(-20))
                {
                    toRemove.Add(kvp.Key);
                }
            }

            lock (_lock)
            {
                foreach (var ip in toRemove)
                {
                    if (_entries.TryRemove(ip, out var entry))
                    {
                        if (entry.LruNode != null)
                        {
                            _lruOrder.Remove(entry.LruNode);
                        }
                    }
                }
            }
        }

        private class RateLimitEntry
        {
            public int PacketCount;
            public DateTime WindowExpiry = DateTime.MinValue;
            public DateTime BlockedUntil = DateTime.MinValue;
            public LinkedListNode<IPAddress>? LruNode;
        }
    }
}
