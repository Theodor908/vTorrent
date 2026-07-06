namespace vTorrent.Abstractions.Records;

/// <summary>
/// Data for updating torrent on shutdown.
/// Following libtorrent's model, we persist both the operational state
/// AND the completion flags (IsFinished, IsSeed) separately, since they
/// are orthogonal concepts.
/// </summary>
public class TorrentShutdownData
{
    public double Progress { get; set; }
    public long TotalUploaded { get; set; }
    public long TotalDownloaded { get; set; }
    public long TotalPayloadUploaded { get; set; }
    public long TotalPayloadDownloaded { get; set; }
    public long ActiveSeconds { get; set; }
    public long SeedingSeconds { get; set; }
    public long LastActiveAt { get; set; }
    public long? LastUpload { get; set; }
    public long? LastDownload { get; set; }

    /// <summary>
    /// Whether all wanted pieces have been downloaded.
    /// Must be persisted to correctly restore seeding state on restart.
    /// </summary>
    public bool IsFinished { get; set; }

    /// <summary>
    /// Whether the torrent is a pure seeder (all pieces downloaded).
    /// </summary>
    public bool IsSeed { get; set; }

    // Orthogonal state dimensions (new)
    public string? TransferPhase { get; set; }
    public string? FileOperation { get; set; }
    public string? UserIntent { get; set; }
    public string? Health { get; set; }
    public string? ErrorMessage { get; set; }
}
