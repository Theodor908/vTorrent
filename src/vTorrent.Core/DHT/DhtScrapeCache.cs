using System.Collections.Concurrent;
using System.Threading.Channels;
using vTorrent.Abstractions.Interfaces;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.DHT;

/// <summary>
/// BEP 33 scrape result cache with background scheduling.
/// Active torrents get piggybacked scrape data (zero extra cost).
/// Inactive torrents are queued for dedicated background lookups.
/// </summary>
public class DhtScrapeCache : IDhtScrapeProvider, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DhtScrapeResult> _cache = new();
    private readonly Channel<byte[]> _scrapeQueue;
    private readonly Func<byte[], Task<DhtScrapeResult?>> _lookupDelegate;
    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;

    public event Action<byte[], DhtScrapeInfo>? ScrapeCompleted;

    public DhtScrapeCache(Func<byte[], Task<DhtScrapeResult?>> lookupDelegate)
    {
        _lookupDelegate = lookupDelegate ?? throw new ArgumentNullException(nameof(lookupDelegate));
        _scrapeQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _backgroundTask = ProcessScrapeQueueAsync(_cts.Token);
    }

    public DhtScrapeInfo? GetScrapeResult(byte[] infoHash)
    {
        string key = Convert.ToHexString(infoHash);
        if (_cache.TryGetValue(key, out var result))
            return new DhtScrapeInfo(result.EstimatedSeeds, result.EstimatedPeers, result.LastUpdated);
        return null;
    }

    public void RequestScrape(byte[] infoHash)
    {
        _scrapeQueue.Writer.TryWrite(infoHash);
    }

    /// <summary>
    /// Update cache from a piggybacked get_peers response (active torrent path).
    /// </summary>
    public void UpdateFromResponse(byte[] infoHash, byte[]? bfsd, byte[]? bfpe)
    {
        if (bfsd == null && bfpe == null) return;
        string key = Convert.ToHexString(infoHash);
        var result = _cache.GetOrAdd(key, _ => new DhtScrapeResult(infoHash));
        result.UnionResponse(bfsd, bfpe);
        var info = new DhtScrapeInfo(result.EstimatedSeeds, result.EstimatedPeers, result.LastUpdated);
        ScrapeCompleted?.Invoke(infoHash, info);
    }

    private async Task ProcessScrapeQueueAsync(CancellationToken ct)
    {
        using var limiter = new SemaphoreSlim(1, 1);
        try
        {
            await foreach (var infoHash in _scrapeQueue.Reader.ReadAllAsync(ct))
            {
                await limiter.WaitAsync(ct);
                try
                {
                    var result = await _lookupDelegate(infoHash);
                    if (result != null)
                    {
                        string key = Convert.ToHexString(infoHash);
                        _cache[key] = result;
                        var info = new DhtScrapeInfo(result.EstimatedSeeds, result.EstimatedPeers, result.LastUpdated);
                        ScrapeCompleted?.Invoke(infoHash, info);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Scrape failed — will be retried on next request */ }
                finally { limiter.Release(); }

                // Randomized delay to avoid synchronized traffic (BEP 33)
                await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(5, 15)), ct);
            }
        }
        catch (OperationCanceledException) { /* Shutdown */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_backgroundTask != null)
        {
            try { await _backgroundTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
    }
}
