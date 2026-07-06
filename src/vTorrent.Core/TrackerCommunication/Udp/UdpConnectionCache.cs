using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;

namespace vTorrent.Core.TrackerCommunication.Udp;

/// <summary>
/// Global cache for UDP tracker connection IDs.
/// Based on libtorrent's connection_cache implementation.
/// Connection IDs are valid for up to 2 minutes per BEP 15. Default TTL set to 120 seconds
/// to maximize cache hits during startup bursts when multiple torrents announce to the same tracker.
/// </summary>
public static class UdpConnectionCache
{
    private static readonly ConcurrentDictionary<string, ConnectionCacheEntry> _cache = new();
    private static Timer _cleanupTimer;
    private static readonly TimeSpan DefaultExpiry = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);

    static UdpConnectionCache()
    {
        // Start periodic cleanup of expired entries
        _cleanupTimer = new Timer(CleanupExpired, null, CleanupInterval, CleanupInterval);
    }

    /// <summary>
    /// Represents a cached connection entry.
    /// </summary>
    public class ConnectionCacheEntry
    {
        public long ConnectionId { get; }
        public DateTime Expires { get; }
        public DateTime LastUsed { get; private set; }
        public int UseCount { get; private set; }

        public ConnectionCacheEntry(long connectionId, DateTime expires)
        {
            ConnectionId = connectionId;
            Expires = expires;
            LastUsed = DateTime.UtcNow;
            UseCount = 0;
        }

        public void RecordUse()
        {
            LastUsed = DateTime.UtcNow;
            Interlocked.Increment(ref _useCount);
        }

        private int _useCount;
    }

    /// <summary>
    /// Gets a cache key for the given endpoint.
    /// </summary>
    private static string GetCacheKey(IPEndPoint endpoint)
    {
        return $"{endpoint.Address}:{endpoint.Port}";
    }

    /// <summary>
    /// Gets a cache key for the given host and port.
    /// </summary>
    private static string GetCacheKey(string host, int port)
    {
        return $"{host}:{port}";
    }

    /// <summary>
    /// Tries to get a cached connection ID for the given tracker endpoint.
    /// </summary>
    /// <param name="endpoint">The tracker endpoint.</param>
    /// <param name="connectionId">The cached connection ID if found and valid.</param>
    /// <returns>True if a valid cached connection ID was found.</returns>
    public static bool TryGetConnectionId(IPEndPoint endpoint, out long connectionId)
    {
        var key = GetCacheKey(endpoint);
        return TryGetConnectionIdByKey(key, out connectionId);
    }

    /// <summary>
    /// Tries to get a cached connection ID for the given tracker host and port.
    /// </summary>
    public static bool TryGetConnectionId(string host, int port, out long connectionId)
    {
        var key = GetCacheKey(host, port);
        return TryGetConnectionIdByKey(key, out connectionId);
    }

    private static bool TryGetConnectionIdByKey(string key, out long connectionId)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow < entry.Expires)
            {
                entry.RecordUse();
                connectionId = entry.ConnectionId;
                return true;
            }

            // Entry expired, remove it
            _cache.TryRemove(key, out _);
        }

        connectionId = 0;
        return false;
    }

    /// <summary>
    /// Caches a connection ID for the given tracker endpoint.
    /// </summary>
    /// <param name="endpoint">The tracker endpoint.</param>
    /// <param name="connectionId">The connection ID to cache.</param>
    /// <param name="expiry">Optional custom expiry time. Defaults to 60 seconds.</param>
    public static void SetConnectionId(IPEndPoint endpoint, long connectionId, TimeSpan? expiry = null)
    {
        var key = GetCacheKey(endpoint);
        SetConnectionIdByKey(key, connectionId, expiry);
    }

    /// <summary>
    /// Caches a connection ID for the given tracker host and port.
    /// </summary>
    public static void SetConnectionId(string host, int port, long connectionId, TimeSpan? expiry = null)
    {
        var key = GetCacheKey(host, port);
        SetConnectionIdByKey(key, connectionId, expiry);
    }

    private static void SetConnectionIdByKey(string key, long connectionId, TimeSpan? expiry)
    {
        var expiryTime = DateTime.UtcNow + (expiry ?? DefaultExpiry);
        _cache[key] = new ConnectionCacheEntry(connectionId, expiryTime);
    }

    /// <summary>
    /// Invalidates the cached connection ID for the given endpoint.
    /// Call this when a connection fails to force reconnection.
    /// </summary>
    public static void Invalidate(IPEndPoint endpoint)
    {
        var key = GetCacheKey(endpoint);
        _cache.TryRemove(key, out _);
    }

    /// <summary>
    /// Invalidates the cached connection ID for the given host and port.
    /// </summary>
    public static void Invalidate(string host, int port)
    {
        var key = GetCacheKey(host, port);
        _cache.TryRemove(key, out _);
    }

    /// <summary>
    /// Clears all cached connection IDs.
    /// </summary>
    public static void Clear()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Gets the number of cached entries.
    /// </summary>
    public static int Count => _cache.Count;

    /// <summary>
    /// Gets statistics about the cache.
    /// </summary>
    public static CacheStatistics GetStatistics()
    {
        var validCount = 0;
        var expiredCount = 0;
        var totalUseCount = 0;
        var now = DateTime.UtcNow;

        foreach (var entry in _cache.Values)
        {
            if (now < entry.Expires)
            {
                validCount++;
                totalUseCount += entry.UseCount;
            }
            else
            {
                expiredCount++;
            }
        }

        return new CacheStatistics(validCount, expiredCount, totalUseCount);
    }

    public record CacheStatistics(int ValidEntries, int ExpiredEntries, int TotalUseCount);

    /// <summary>
    /// Periodic cleanup of expired entries.
    /// </summary>
    private static void CleanupExpired(object state)
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _cache)
        {
            if (now >= kvp.Value.Expires)
            {
                _cache.TryRemove(kvp.Key, out _);
            }
        }
    }
}
