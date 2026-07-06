using System.Collections.Generic;

namespace vTorrent.Core.DHT;

/// <summary>
/// Result of an active BEP 51 sample_infohashes query.
/// </summary>
public class SampleInfohashesResult
{
    /// <summary>
    /// Collected infohash samples (each 20 bytes).
    /// </summary>
    public List<byte[]> Infohashes { get; set; } = new();

    /// <summary>
    /// Per-node total infohash counts reported by each responding node.
    /// </summary>
    public Dictionary<NodeId, int> NodeTotals { get; set; } = new();

    /// <summary>
    /// Minimum interval reported by any responding node.
    /// </summary>
    public int MinIntervalSeconds { get; set; }
}
