namespace vTorrent.Core.DHT;

/// <summary>
/// Statistics about the DHT node.
/// </summary>
public struct DhtNodeStats
{
    public NodeId NodeId { get; set; }
    public bool IsRunning { get; set; }
    public int NumBuckets { get; set; }
    public int LiveNodes { get; set; }
    public int ReplacementNodes { get; set; }
    public int ConfirmedNodes { get; set; }
    public int RouterNodes { get; set; }
    public int PendingQueries { get; set; }
    public int StoredInfoHashes { get; set; }
    public int StoredPeers { get; set; }
    public int TrackedIps { get; set; }
    public int BlockedIps { get; set; }
}
