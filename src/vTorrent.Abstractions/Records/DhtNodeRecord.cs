namespace vTorrent.Abstractions.Records;

/// <summary>
/// Database record for DHT routing table nodes
/// </summary>
public class DhtNodeRecord
{
    public string NodeId { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; }
    public int RttMs { get; set; }
    public long LastSeen { get; set; }
}
