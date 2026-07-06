namespace vTorrent.Abstractions.Records;

/// <summary>
/// Database record for known peers
/// </summary>
public class KnownPeerRecord
{
    public string InfoHash { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Source { get; set; } = "tracker";
    public long? LastSeen { get; set; }
    public long? LastConnected { get; set; }
    public int FailedCount { get; set; }
    public int TrustPoints { get; set; }
    public long TotalUploaded { get; set; }
    public long TotalDownloaded { get; set; }
}
