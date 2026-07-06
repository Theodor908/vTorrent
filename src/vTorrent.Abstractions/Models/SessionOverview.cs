namespace vTorrent.Abstractions.Models;

/// <summary>
/// Immutable point-in-time snapshot of session-wide statistics.
/// The batch stats event carries this alongside changed torrent snapshots.
/// </summary>
public record SessionOverview
{
    // Rates (raw)
    public int GlobalDownloadRate { get; init; }
    public int GlobalUploadRate { get; init; }

    // Session totals
    public long SessionDownloaded { get; init; }
    public long SessionUploaded { get; init; }
    public long AllTimeDownloaded { get; init; }
    public long AllTimeUploaded { get; init; }

    // Torrent counts
    public int TotalTorrents { get; init; }
    public int ActiveDownloads { get; init; }
    public int ActiveUploads { get; init; }
    public int PausedTorrents { get; init; }
    public int CheckingTorrents { get; init; }
    public int QueuedTorrents { get; init; }
    public int ErrorTorrents { get; init; }

    // Connections
    public int ConnectedPeers { get; init; }
    public int TotalConnections { get; init; }
    public int HalfOpenConnections { get; init; }

    // DHT
    public int DhtNodes { get; init; }
    public bool DhtEnabled { get; init; }

    // Disk
    public int DiskReadQueue { get; init; }
    public int DiskWriteQueue { get; init; }
    public long DiskBytesRead { get; init; }
    public long DiskBytesWritten { get; init; }

    // Network
    public int ListenPort { get; init; }
    public bool PortOpen { get; init; }
    public string? ExternalIp { get; init; }
    public int DownloadLimit { get; init; }
    public int UploadLimit { get; init; }

    // Session state
    public bool IsPaused { get; init; }
    public TimeSpan Uptime { get; init; }
    public long FreeSpace { get; init; }
}
