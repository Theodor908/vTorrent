namespace vTorrent.Abstractions.Models;

/// <summary>
/// DHT scrape result — estimated seed/peer counts from BEP 33 bloom filters.
/// </summary>
public record DhtScrapeInfo(int EstimatedSeeds, int EstimatedPeers, DateTime LastUpdated);
