namespace vTorrent.Abstractions.Records;

/// <summary>
/// Data for periodic statistics updates during auto-save.
/// Unlike TorrentShutdownData, this is used for incremental saves while running.
/// Now includes IsFinished/IsSeed for crash resilience.
/// </summary>
public class TorrentStatsUpdate
{
    public long TotalUploaded { get; init; }
    public long TotalDownloaded { get; init; }
    public long TotalPayloadUploaded { get; init; }
    public long TotalPayloadDownloaded { get; init; }
    public double Progress { get; init; }
    public long ActiveSeconds { get; init; }
    public long SeedingSeconds { get; init; }
    public bool IsFinished { get; init; }
    public bool IsSeed { get; init; }

    public TorrentStatsUpdate(long totalUploaded, long totalDownloaded, double progress,
        long activeSeconds, long seedingSeconds, bool isFinished = false, bool isSeed = false,
        long totalPayloadUploaded = 0, long totalPayloadDownloaded = 0)
    {
        TotalUploaded = totalUploaded;
        TotalDownloaded = totalDownloaded;
        Progress = progress;
        ActiveSeconds = activeSeconds;
        SeedingSeconds = seedingSeconds;
        IsFinished = isFinished;
        IsSeed = isSeed;
        TotalPayloadUploaded = totalPayloadUploaded;
        TotalPayloadDownloaded = totalPayloadDownloaded;
    }
}
