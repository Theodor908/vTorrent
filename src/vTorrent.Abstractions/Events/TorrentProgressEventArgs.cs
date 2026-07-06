using System;

namespace vTorrent.Abstractions.Events;

public class TorrentProgressEventArgs : EventArgs
{
    public int PiecesCompleted { get; }
    public int TotalPieces { get; }
    public long BytesDownloaded { get; }      // Total downloaded (includes unverified)
    public long BytesVerified { get; }        // Only verified bytes (written to disk)
    public long BytesInProgress { get; }      // Not yet verified
    public long BytesEffective { get; }       // Verified + In-progress
    public long BytesUploaded { get; }        // Total uploaded
    public long TotalBytes { get; }
    public double DownloadRate { get; }
    public double UploadRate { get; }         // Current upload speed
    public int ConnectedPeers { get; }
    public int ConnectedSeeds { get; }        // Connected peers that are seeds
    public int UnchokedPeers { get; }         // Peers we've unchoked
    public int Seeders { get; }               // Tracker-reported seeders
    public int Leechers { get; }              // Tracker-reported leechers
    public int PendingRequests { get; }
    public int InProgressPieces { get; }
    public long FailedBytes { get; }          // Bytes that failed hash verification
    /// <summary>
    /// Progress based on verified pieces (most accurate).
    /// </summary>
    public double Progress => TotalPieces > 0 ? (double)PiecesCompleted / TotalPieces : 0;
    /// <summary>
    /// Progress including in-progress bytes (for UI responsiveness).
    /// </summary>
    public double EffectiveProgress => TotalBytes > 0 ? (double)BytesEffective / TotalBytes : 0;
    /// <summary>
    /// Progress based on verified bytes only (libtorrent-style total_wanted_done).
    /// </summary>
    public double VerifiedProgress => TotalBytes > 0 ? (double)BytesVerified / TotalBytes : 0;

    public TorrentProgressEventArgs(
        int piecesCompleted,
        int totalPieces,
        long bytesDownloaded,
        long bytesVerified,
        long bytesInProgress,
        long bytesUploaded,
        long totalBytes,
        double downloadRate,
        double uploadRate,
        int connectedPeers,
        int connectedSeeds,
        int unchokedPeers,
        int seeders,
        int leechers,
        int pendingRequests,
        int inProgressPieces,
        long failedBytes = 0)
    {
        PiecesCompleted = piecesCompleted;
        TotalPieces = totalPieces;
        BytesDownloaded = bytesDownloaded;
        BytesVerified = bytesVerified;
        BytesInProgress = bytesInProgress;
        BytesEffective = bytesVerified + bytesInProgress;  // Effective = verified + in-progress
        BytesUploaded = bytesUploaded;
        TotalBytes = totalBytes;
        DownloadRate = downloadRate;
        UploadRate = uploadRate;
        ConnectedPeers = connectedPeers;
        ConnectedSeeds = connectedSeeds;
        UnchokedPeers = unchokedPeers;
        Seeders = seeders;
        Leechers = leechers;
        PendingRequests = pendingRequests;
        InProgressPieces = inProgressPieces;
        FailedBytes = failedBytes;
    }
}