namespace vTorrent.Abstractions.Records;

/// <summary>
/// Database record for banned peers
/// </summary>
public class BannedPeerRecord
{
    public string Ip { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public long BannedAt { get; set; }
}
