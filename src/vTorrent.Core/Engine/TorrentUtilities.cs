using System;
using vTorrent.Bencode.Torrents;

namespace vTorrent.Core.Engine;

/// <summary>
/// Shared utility methods for torrent operations.
/// </summary>
public static class TorrentUtilities
{
    /// <summary>
    /// Calculate the size of a specific piece in the torrent.
    /// </summary>
    public static long GetPieceSize(TorrentInfo info, int pieceIndex)
    {
        if (info == null)
            throw new ArgumentNullException(nameof(info));

        if (pieceIndex < 0 || pieceIndex >= info.PieceCount)
            throw new ArgumentOutOfRangeException(nameof(pieceIndex));

        // Last piece may be smaller
        if (pieceIndex == info.PieceCount - 1)
        {
            var lastPieceSize = info.TotalSize % info.PieceLength;
            return lastPieceSize == 0 ? info.PieceLength : lastPieceSize;
        }

        return info.PieceLength;
    }

    /// <summary>
    /// Format bytes to human-readable string (B, KB, MB, GB, TB).
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";

        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Format transfer rate to human-readable string (B/s, KB/s, MB/s, GB/s).
    /// </summary>
    public static string FormatRate(double bytesPerSecond)
    {
        return $"{FormatBytes((long)bytesPerSecond)}/s";
    }

    /// <summary>
    /// Calculate estimated time remaining based on bytes remaining and download rate.
    /// </summary>
    public static TimeSpan CalculateETA(long bytesRemaining, double downloadRate)
    {
        if (downloadRate <= 0 || bytesRemaining <= 0)
            return TimeSpan.MaxValue;

        return TimeSpan.FromSeconds(bytesRemaining / downloadRate);
    }

    /// <summary>
    /// Format TimeSpan to human-readable ETA string.
    /// </summary>
    public static string FormatETA(TimeSpan timeSpan)
    {
        if (timeSpan == TimeSpan.MaxValue)
            return "∞";

        if (timeSpan.TotalDays >= 1)
            return $"{(int)timeSpan.TotalDays}d {timeSpan.Hours}h";

        if (timeSpan.TotalHours >= 1)
            return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m";

        if (timeSpan.TotalMinutes >= 1)
            return $"{(int)timeSpan.TotalMinutes}m {timeSpan.Seconds}s";

        return $"{(int)timeSpan.TotalSeconds}s";
    }
}
