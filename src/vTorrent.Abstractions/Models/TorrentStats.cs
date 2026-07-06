using System;
using vTorrent.Abstractions.Enums;

namespace vTorrent.Abstractions.Models;

public class TorrentStats
{
    public TransferPhase Phase { get; init; }
    public UserIntent Intent { get; init; }
    public double Progress { get; init; }
    public int PiecesCompleted { get; init; }
    public int TotalPieces { get; init; }
    public long BytesDownloaded { get; init; }
    public long BytesUploaded { get; init; }
    public long TotalSize { get; init; }
    public long BytesRemaining { get; init; }
    public double DownloadRate { get; init; }
    public int ConnectedPeers { get; init; }
    public int TotalSeeders { get; init; }
    public int TotalLeechers { get; init; }
    public DateTime StartTime { get; init; }
    public TimeSpan ElapsedTime { get; init; }

    public TimeSpan EstimatedTimeRemaining
    {
        get
        {
            if (DownloadRate <= 0 || BytesRemaining <= 0)
                return TimeSpan.MaxValue;
            return TimeSpan.FromSeconds(BytesRemaining / DownloadRate);
        }
    }
}
