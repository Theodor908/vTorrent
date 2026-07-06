namespace vTorrent.Core.DHT;

/// <summary>
/// Per-infohash aggregate of BEP 33 bloom filters from multiple DHT nodes.
/// </summary>
public class DhtScrapeResult
{
    public byte[] InfoHash { get; }
    public BloomFilter SeedFilter { get; } = new();
    public BloomFilter PeerFilter { get; } = new();
    public int EstimatedSeeds => Math.Max(0, (int)SeedFilter.EstimateCount());
    public int EstimatedPeers => Math.Max(0, (int)PeerFilter.EstimateCount());
    public DateTime LastUpdated { get; set; }

    public DhtScrapeResult(byte[] infoHash)
    {
        InfoHash = infoHash ?? throw new ArgumentNullException(nameof(infoHash));
    }

    public void UnionResponse(byte[]? bfsd, byte[]? bfpe)
    {
        if (bfsd?.Length == BloomFilter.FilterSizeBytes)
        {
            var filter = new BloomFilter(bfsd);
            if (!filter.IsSaturated())
                SeedFilter.Union(bfsd);
        }
        if (bfpe?.Length == BloomFilter.FilterSizeBytes)
        {
            var filter = new BloomFilter(bfpe);
            if (!filter.IsSaturated())
                PeerFilter.Union(bfpe);
        }
        LastUpdated = DateTime.UtcNow;
    }
}
