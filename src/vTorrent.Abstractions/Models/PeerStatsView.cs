namespace vTorrent.Abstractions.Models;

public record PeerStatsView
{
    public string Endpoint { get; init; } = "";
    public string? Client { get; init; }
    public long PayloadDownloaded { get; init; }
    public long PayloadUploaded { get; init; }
    public int PayloadDownloadRate { get; init; }
    public int PayloadUploadRate { get; init; }
    public float Progress { get; init; }
    public string Flags { get; init; } = "";
}
