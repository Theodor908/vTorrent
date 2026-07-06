namespace vTorrent.Core.DHT;

/// <summary>
/// Result of a BEP 51 infohash sample query.
/// </summary>
/// <param name="Samples">N * 20 bytes, concatenated infohashes</param>
/// <param name="TotalCount">Total infohashes stored on this node</param>
/// <param name="IntervalSeconds">Recommended re-query interval</param>
public readonly record struct DhtSampleResult(
    byte[] Samples,
    int TotalCount,
    int IntervalSeconds
);
