using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace vTorrent.Core.TrackerCommunication;

/// <summary>
/// Thread-safe DNS cache for tracker hostnames.
/// Reduces DNS lookup latency for repeated announces to the same trackers.
/// Based on libtorrent's resolver_interface patterns.
/// </summary>
public class DnsCache : IDisposable
{
    private readonly ConcurrentDictionary<string, DnsCacheEntry> _cache = new();
    private readonly TimeSpan _defaultTtl;
    private readonly TimeSpan _negativeTtl;
    private readonly ILogger<DnsCache> _logger;
    private Timer _cleanupTimer;
    private bool _disposed;

    /// <summary>
    /// Represents a cached DNS entry.
    /// </summary>
    public class DnsCacheEntry
    {
        public IPAddress[] Addresses { get; }
        public DateTime Expires { get; }
        public DateTime LastUsed { get; private set; }
        public int HitCount => _hitCount;
        public bool IsNegativeEntry { get; }

        public DnsCacheEntry(IPAddress[] addresses, DateTime expires, bool isNegative = false)
        {
            Addresses = addresses ?? Array.Empty<IPAddress>();
            Expires = expires;
            LastUsed = DateTime.UtcNow;
            IsNegativeEntry = isNegative;
        }

        public void RecordHit()
        {
            LastUsed = DateTime.UtcNow;
            Interlocked.Increment(ref _hitCount);
        }

        private int _hitCount;
    }

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolver;

    /// <summary>
    /// Creates a DNS cache with the specified TTL settings.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="defaultTtl">How long successful lookups are cached. Default: 5 minutes.</param>
    /// <param name="negativeTtl">How long failed lookups are cached. Default: 30 seconds.</param>
    /// <param name="resolver">Optional custom resolver func for testing. Receives hostname and CancellationToken.</param>
    public DnsCache(ILogger<DnsCache> logger = null, TimeSpan? defaultTtl = null, TimeSpan? negativeTtl = null,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver = null)
    {
        _logger = logger;
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(5);
        _negativeTtl = negativeTtl ?? TimeSpan.FromSeconds(30);
        _resolver = resolver;

        // Start cleanup timer
        _cleanupTimer = new Timer(CleanupExpired, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Creates a DNS cache with a simple resolver func (hostname-only, no cancellation).
    /// Convenience constructor for testing.
    /// </summary>
    public DnsCache(TimeSpan ttl, Func<string, Task<IPAddress[]>> resolver)
    {
        _logger = null;
        _defaultTtl = ttl;
        _negativeTtl = TimeSpan.FromSeconds(30);
        _resolver = (hostname, _) => resolver(hostname);
        _cleanupTimer = new Timer(CleanupExpired, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Resolves a hostname to IP addresses, using the cache when possible.
    /// </summary>
    /// <param name="hostname">The hostname to resolve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of resolved IP addresses, or empty array if resolution fails.</returns>
    public async Task<IPAddress[]> ResolveAsync(string hostname, CancellationToken cancellationToken = default)
    {
        return await ResolveAsync(hostname, cacheOnly: false, cancellationToken);
    }

    /// <summary>
    /// Resolves a hostname to IP addresses.
    /// </summary>
    /// <param name="hostname">The hostname to resolve.</param>
    /// <param name="cacheOnly">If true, only returns cached results (for shutdown scenarios).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Array of resolved IP addresses, or empty array if resolution fails.</returns>
    public async Task<IPAddress[]> ResolveAsync(string hostname, bool cacheOnly, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return Array.Empty<IPAddress>();

        // Normalize hostname
        hostname = hostname.ToLowerInvariant();

        // Check if it's already an IP address
        if (IPAddress.TryParse(hostname, out var parsedIp))
        {
            return new[] { parsedIp };
        }

        // Try to get from cache
        if (_cache.TryGetValue(hostname, out var entry))
        {
            if (DateTime.UtcNow < entry.Expires)
            {
                entry.RecordHit();

                if (entry.IsNegativeEntry)
                {
                    _logger?.LogDebug("DNS cache negative hit for {Hostname}", hostname);
                    return Array.Empty<IPAddress>();
                }

                _logger?.LogDebug("DNS cache hit for {Hostname}: {Addresses}", hostname, string.Join(", ", entry.Addresses.Select(a => a.ToString())));
                return entry.Addresses;
            }

            // Entry expired
            _cache.TryRemove(hostname, out _);
        }

        // Cache-only mode returns whatever we had (even if expired) or empty
        if (cacheOnly)
        {
            _logger?.LogDebug("DNS cache-only mode, no valid entry for {Hostname}", hostname);
            return entry?.Addresses ?? Array.Empty<IPAddress>();
        }

        // Perform DNS lookup
        try
        {
            _logger?.LogDebug("Resolving DNS for {Hostname}...", hostname);

            var addresses = _resolver != null
                ? await _resolver(hostname, cancellationToken).ConfigureAwait(false)
                : await Dns.GetHostAddressesAsync(hostname, cancellationToken);

            if (addresses.Length > 0)
            {
                // Sort addresses: IPv4 first for better compatibility
                var sortedAddresses = addresses
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .ToArray();

                _cache[hostname] = new DnsCacheEntry(sortedAddresses, DateTime.UtcNow + _defaultTtl);

                _logger?.LogDebug("DNS resolved {Hostname} to {Count} addresses (cached for {Ttl})",
                    hostname, sortedAddresses.Length, _defaultTtl);

                return sortedAddresses;
            }
            else
            {
                // Cache negative result
                _cache[hostname] = new DnsCacheEntry(Array.Empty<IPAddress>(), DateTime.UtcNow + _negativeTtl, isNegative: true);
                _logger?.LogDebug("DNS resolution returned no addresses for {Hostname}", hostname);
                return Array.Empty<IPAddress>();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DNS resolution failed for {Hostname}", hostname);

            // Cache negative result
            _cache[hostname] = new DnsCacheEntry(Array.Empty<IPAddress>(), DateTime.UtcNow + _negativeTtl, isNegative: true);
            return Array.Empty<IPAddress>();
        }
    }

    /// <summary>
    /// Resolves a hostname synchronously, using the cache when possible.
    /// </summary>
    public IPAddress[] Resolve(string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return Array.Empty<IPAddress>();

        hostname = hostname.ToLowerInvariant();

        if (IPAddress.TryParse(hostname, out var parsedIp))
        {
            return new[] { parsedIp };
        }

        // Try cache first
        if (_cache.TryGetValue(hostname, out var entry) && DateTime.UtcNow < entry.Expires)
        {
            entry.RecordHit();
            return entry.IsNegativeEntry ? Array.Empty<IPAddress>() : entry.Addresses;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(hostname);
            if (addresses.Length > 0)
            {
                var sortedAddresses = addresses
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .ToArray();

                _cache[hostname] = new DnsCacheEntry(sortedAddresses, DateTime.UtcNow + _defaultTtl);
                return sortedAddresses;
            }
        }
        catch
        {
            _cache[hostname] = new DnsCacheEntry(Array.Empty<IPAddress>(), DateTime.UtcNow + _negativeTtl, isNegative: true);
        }

        return Array.Empty<IPAddress>();
    }

    /// <summary>
    /// Gets the preferred IP address for a hostname (IPv4 preferred).
    /// </summary>
    public async Task<IPAddress> ResolvePreferredAsync(string hostname, CancellationToken cancellationToken = default)
    {
        var addresses = await ResolveAsync(hostname, cancellationToken);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
    }

    /// <summary>
    /// Pre-populates the cache with a hostname resolution.
    /// Useful during startup to resolve trackers in advance.
    /// </summary>
    public async Task PrewarmAsync(string hostname, CancellationToken cancellationToken = default)
    {
        await ResolveAsync(hostname, cancellationToken);
    }

    /// <summary>
    /// Pre-populates the cache with multiple hostname resolutions in parallel.
    /// </summary>
    public async Task PrewarmAsync(IEnumerable<string> hostnames, CancellationToken cancellationToken = default)
    {
        var tasks = hostnames
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => ResolveAsync(h, cancellationToken));

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Invalidates the cache entry for a specific hostname.
    /// </summary>
    public void Invalidate(string hostname)
    {
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            _cache.TryRemove(hostname.ToLowerInvariant(), out _);
        }
    }

    /// <summary>
    /// Clears all cached entries.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Gets the number of cached entries.
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public DnsCacheStatistics GetStatistics()
    {
        var now = DateTime.UtcNow;
        int validEntries = 0;
        int expiredEntries = 0;
        int negativeEntries = 0;
        int totalHits = 0;

        foreach (var entry in _cache.Values)
        {
            if (now < entry.Expires)
            {
                validEntries++;
                if (entry.IsNegativeEntry)
                    negativeEntries++;
            }
            else
            {
                expiredEntries++;
            }
            totalHits += entry.HitCount;
        }

        return new DnsCacheStatistics(validEntries, expiredEntries, negativeEntries, totalHits);
    }

    public record DnsCacheStatistics(int ValidEntries, int ExpiredEntries, int NegativeEntries, int TotalHitCount);

    private void CleanupExpired(object state)
    {
        if (_disposed)
            return;

        var now = DateTime.UtcNow;
        foreach (var kvp in _cache)
        {
            if (now >= kvp.Value.Expires)
            {
                _cache.TryRemove(kvp.Key, out _);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cleanupTimer?.Dispose();
        _cache.Clear();
    }
}

/// <summary>
/// Static accessor for a shared DNS cache instance.
/// </summary>
public static class SharedDnsCache
{
    private static DnsCache _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets or creates the shared DNS cache instance.
    /// </summary>
    public static DnsCache Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new DnsCache();
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// Initializes the shared DNS cache with a custom configuration.
    /// </summary>
    public static void Initialize(ILogger<DnsCache> logger = null, TimeSpan? defaultTtl = null, TimeSpan? negativeTtl = null)
    {
        lock (_lock)
        {
            _instance?.Dispose();
            _instance = new DnsCache(logger, defaultTtl, negativeTtl);
        }
    }

    /// <summary>
    /// Shuts down the shared DNS cache.
    /// </summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            _instance?.Dispose();
            _instance = null;
        }
    }
}
