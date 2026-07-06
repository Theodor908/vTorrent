namespace vTorrent.Abstractions.Records;

/// <summary>
/// Database record for statistics snapshots
/// </summary>
public class StatisticsSnapshotRecord
{
    public long Id { get; set; }
    public string? InfoHash { get; set; } // null for session-wide
    public long Timestamp { get; set; }
    public int? DownloadRate { get; set; }
    public int? UploadRate { get; set; }
    public long? Downloaded { get; set; }
    public long? Uploaded { get; set; }
    public int? Peers { get; set; }
    public int? Seeds { get; set; }
}
