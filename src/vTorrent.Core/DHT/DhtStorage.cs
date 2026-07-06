using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.DHT
{
    /// <summary>
    /// Stores peer information for info_hashes announced to this DHT node.
    /// Implements per BEP 5 with expiration and capacity limits.
    /// </summary>
    public class DhtDefaultStorage : IDhtStorage
    {
        private readonly IOptionsMonitor<DhtSettings> _dhtMonitor;
        private readonly ConcurrentDictionary<string, InfoHashEntry> _storage;
        private readonly object _cleanupLock = new();
        private DateTime _lastCleanup = DateTime.UtcNow;
        private byte[] _cachedSamples = Array.Empty<byte>();
        private DateTime _lastSampleRefresh = DateTime.MinValue;

        /// <summary>
        /// Number of info_hashes currently stored.
        /// </summary>
        public int InfoHashCount => _storage.Count;

        /// <summary>
        /// Total number of peers stored across all info_hashes.
        /// </summary>
        public int TotalPeerCount => _storage.Values.Sum(e => e.PeerCount);

        public DhtDefaultStorage(IOptionsMonitor<DhtSettings> dhtMonitor)
        {
            _dhtMonitor = dhtMonitor ?? throw new ArgumentNullException(nameof(dhtMonitor));
            _storage = new ConcurrentDictionary<string, InfoHashEntry>();
        }

        /// <summary>
        /// Gets peers for an info_hash.
        /// </summary>
        public List<IPEndPoint> GetPeers(byte[] infoHash, int maxPeers = 0)
        {
            if (infoHash == null || infoHash.Length != 20)
                return new List<IPEndPoint>();

            string key = Convert.ToHexString(infoHash);
            if (maxPeers == 0) maxPeers = _dhtMonitor.CurrentValue.MaxPeersReply;

            if (_storage.TryGetValue(key, out var entry))
            {
                return entry.GetPeers(maxPeers);
            }

            return new List<IPEndPoint>();
        }

        /// <summary>
        /// Checks if we have peers for an info_hash.
        /// </summary>
        public bool HasPeers(byte[] infoHash)
        {
            if (infoHash == null || infoHash.Length != 20)
                return false;

            string key = Convert.ToHexString(infoHash);
            return _storage.TryGetValue(key, out var entry) && entry.PeerCount > 0;
        }

        /// <summary>
        /// Announces a peer for an info_hash.
        /// </summary>
        public bool AnnouncePeer(byte[] infoHash, IPEndPoint peer, bool isSeed = false)
        {
            if (infoHash == null || infoHash.Length != 20)
                return false;
            if (peer == null)
                return false;

            // Check capacity limits
            MaybeCleanup();

            var settings = _dhtMonitor.CurrentValue;

            if (_storage.Count >= settings.MaxInfoHashes && !_storage.ContainsKey(Convert.ToHexString(infoHash)))
            {
                // At capacity, can't add new info_hash
                return false;
            }

            if (TotalPeerCount >= settings.MaxTotalPeers)
            {
                // At total peer capacity
                return false;
            }

            string key = Convert.ToHexString(infoHash);
            var entry = _storage.GetOrAdd(key, _ => new InfoHashEntry(infoHash, settings.MaxPeersPerInfoHash));

            return entry.AddPeer(peer, TimeSpan.FromMilliseconds(DhtConstants.PeerExpirationMs), isSeed);
        }

        /// <summary>
        /// Gets a bloom filter of seed peers for an info_hash (BEP 33).
        /// </summary>
        public BloomFilter GetSeedBloomFilter(byte[] infoHash)
        {
            if (infoHash == null || infoHash.Length != 20) return new BloomFilter();
            string key = Convert.ToHexString(infoHash);
            if (_storage.TryGetValue(key, out var entry))
                return entry.BuildSeedBloomFilter();
            return new BloomFilter();
        }

        /// <summary>
        /// Gets a bloom filter of all peers for an info_hash (BEP 33).
        /// </summary>
        public BloomFilter GetPeerBloomFilter(byte[] infoHash)
        {
            if (infoHash == null || infoHash.Length != 20) return new BloomFilter();
            string key = Convert.ToHexString(infoHash);
            if (_storage.TryGetValue(key, out var entry))
                return entry.BuildPeerBloomFilter();
            return new BloomFilter();
        }

        /// <summary>
        /// Removes expired peers and empty entries.
        /// </summary>
        public void Cleanup()
        {
            lock (_cleanupLock)
            {
                var keysToRemove = new List<string>();

                foreach (var kvp in _storage)
                {
                    kvp.Value.RemoveExpired();

                    if (kvp.Value.PeerCount == 0)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _storage.TryRemove(key, out _);
                }

                _lastCleanup = DateTime.UtcNow;
            }
        }

        private void MaybeCleanup()
        {
            // Cleanup every minute
            if ((DateTime.UtcNow - _lastCleanup).TotalMinutes >= 1)
            {
                Cleanup();
            }
        }

        /// <summary>
        /// Gets storage statistics.
        /// </summary>
        public DhtStorageStats GetStats()
        {
            return new DhtStorageStats
            {
                InfoHashCount = InfoHashCount,
                TotalPeerCount = TotalPeerCount,
                MaxInfoHashes = _dhtMonitor.CurrentValue.MaxInfoHashes,
                MaxTotalPeers = _dhtMonitor.CurrentValue.MaxTotalPeers
            };
        }

        /// <summary>
        /// Clears all stored data.
        /// </summary>
        public void Clear()
        {
            _storage.Clear();
        }

        /// <summary>
        /// BEP 51: Returns a cached random sample of stored infohashes.
        /// Sample is refreshed at the configured interval.
        /// </summary>
        public DhtSampleResult GetInfohashesSample()
        {
            RefreshSampleIfNeeded();
            return new DhtSampleResult(
                _cachedSamples,
                _storage.Count,
                _dhtMonitor.CurrentValue.SampleInfohashesIntervalSeconds
            );
        }

        private void RefreshSampleIfNeeded()
        {
            if ((DateTime.UtcNow - _lastSampleRefresh).TotalSeconds < _dhtMonitor.CurrentValue.SampleInfohashesIntervalSeconds
                && _lastSampleRefresh > DateTime.MinValue)
                return;

            var keys = _storage.Keys.ToList();
            if (keys.Count == 0)
            {
                _cachedSamples = Array.Empty<byte>();
                _lastSampleRefresh = DateTime.UtcNow;
                return;
            }

            var rng = Random.Shared;
            int sampleCount = Math.Min(keys.Count, _dhtMonitor.CurrentValue.MaxSampleCount);

            // Fisher-Yates partial shuffle for sampling
            for (int i = 0; i < sampleCount; i++)
            {
                int j = rng.Next(i, keys.Count);
                (keys[i], keys[j]) = (keys[j], keys[i]);
            }

            var samples = new byte[sampleCount * 20];
            for (int i = 0; i < sampleCount; i++)
            {
                Convert.FromHexString(keys[i]).CopyTo(samples, i * 20);
            }

            _cachedSamples = samples;
            _lastSampleRefresh = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Stores peers for a single info_hash.
    /// </summary>
    internal class InfoHashEntry
    {
        private readonly byte[] _infoHash;
        private readonly int _maxPeers;
        private readonly ConcurrentDictionary<string, PeerEntry> _peers;

        public int PeerCount => _peers.Count;

        public InfoHashEntry(byte[] infoHash, int maxPeers)
        {
            _infoHash = infoHash;
            _maxPeers = maxPeers;
            _peers = new ConcurrentDictionary<string, PeerEntry>();
        }

        public bool AddPeer(IPEndPoint peer, TimeSpan expiration, bool isSeed = false)
        {
            string key = $"{peer.Address}:{peer.Port}";

            if (_peers.TryGetValue(key, out var existing))
            {
                // Update expiration
                existing.ExpiresAt = DateTime.UtcNow + expiration;
                existing.IsSeed = isSeed;
                return true;
            }

            if (_peers.Count >= _maxPeers)
            {
                // At capacity, try to remove expired first
                RemoveExpired();

                if (_peers.Count >= _maxPeers)
                {
                    // Still at capacity, remove oldest
                    var oldest = _peers.Values.OrderBy(p => p.AddedAt).FirstOrDefault();
                    if (oldest != null)
                    {
                        _peers.TryRemove($"{oldest.Endpoint.Address}:{oldest.Endpoint.Port}", out _);
                    }
                }
            }

            var entry = new PeerEntry
            {
                Endpoint = peer,
                AddedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow + expiration,
                IsSeed = isSeed
            };

            return _peers.TryAdd(key, entry);
        }

        public List<IPEndPoint> GetPeers(int maxPeers)
        {
            RemoveExpired();

            return _peers.Values
                .OrderByDescending(p => p.AddedAt) // Prefer recently added
                .Take(maxPeers)
                .Select(p => p.Endpoint)
                .ToList();
        }

        internal BloomFilter BuildSeedBloomFilter()
        {
            var filter = new BloomFilter();
            foreach (var peer in _peers.Values)
            {
                if (peer.IsSeed)
                    filter.Add(peer.Endpoint.Address);
            }
            return filter;
        }

        internal BloomFilter BuildPeerBloomFilter()
        {
            var filter = new BloomFilter();
            foreach (var peer in _peers.Values)
                filter.Add(peer.Endpoint.Address);
            return filter;
        }

        public void RemoveExpired()
        {
            var now = DateTime.UtcNow;
            var keysToRemove = _peers
                .Where(kvp => kvp.Value.ExpiresAt < now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _peers.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// A single peer entry with expiration.
    /// </summary>
    internal class PeerEntry
    {
        public IPEndPoint Endpoint { get; set; }
        public DateTime AddedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool IsSeed { get; set; }
    }

    /// <summary>
    /// DHT storage statistics.
    /// </summary>
    public struct DhtStorageStats
    {
        public int InfoHashCount { get; set; }
        public int TotalPeerCount { get; set; }
        public int MaxInfoHashes { get; set; }
        public int MaxTotalPeers { get; set; }
    }
}
