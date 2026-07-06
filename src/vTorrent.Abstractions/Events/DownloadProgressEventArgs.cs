using System;

namespace vTorrent.Abstractions.Events;

public class DownloadProgressEventArgs : EventArgs
{
    public int PiecesCompleted { get; }
    public int TotalPieces { get; }
    public long BytesDownloaded { get; }      // Verified bytes (completed pieces)
    public long BytesInProgress { get; }      // Downloaded but not yet verified
    public long BytesEffective { get; }       // Verified + In-progress
    public long TotalBytes { get; }
    public double DownloadRate { get; }
    public int PendingRequests { get; }
    public int InProgressPieces { get; }
    public long FailedBytes { get; }          // Bytes that failed hash verification
    public double Progress => TotalPieces > 0 ? (double)PiecesCompleted / TotalPieces : 0;
    public double EffectiveProgress => TotalBytes > 0 ? (double)BytesEffective / TotalBytes : 0;

    public DownloadProgressEventArgs(
        int piecesCompleted,
        int totalPieces,
        long bytesDownloaded,
        long bytesInProgress,
        long totalBytes,
        double downloadRate,
        int pendingRequests,
        int inProgressPieces,
        long failedBytes = 0)
    {
        PiecesCompleted = piecesCompleted;
        TotalPieces = totalPieces;
        BytesDownloaded = bytesDownloaded;
        BytesInProgress = bytesInProgress;
        BytesEffective = bytesDownloaded + bytesInProgress;
        TotalBytes = totalBytes;
        DownloadRate = downloadRate;
        PendingRequests = pendingRequests;
        InProgressPieces = inProgressPieces;
        FailedBytes = failedBytes;
    }
}