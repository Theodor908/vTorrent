using vTorrent.Abstractions.Models;

namespace vTorrent.Abstractions.Interfaces;

/// <summary>
/// Provides DHT-based swarm health estimates via BEP 33 bloom filters.
/// Abstracts away bloom filter internals — consumers see only estimated counts.
/// </summary>
public interface IDhtScrapeProvider
{
    DhtScrapeInfo? GetScrapeResult(byte[] infoHash);
    void RequestScrape(byte[] infoHash);
    event Action<byte[], DhtScrapeInfo>? ScrapeCompleted;
}
