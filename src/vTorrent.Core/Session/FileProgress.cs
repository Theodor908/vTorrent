using System;

namespace vTorrent.Core.Session;

/// <summary>
/// Tracks download progress for a single file within a torrent.
/// Maps file byte ranges to piece indices for progress calculation.
/// </summary>
public class FileProgress
{
    /// <summary>
    /// Index of this file in the torrent's file list
    /// </summary>
    public int FileIndex { get; }

    /// <summary>
    /// File path relative to torrent root
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Total file size in bytes
    /// </summary>
    public long Size { get; }

    /// <summary>
    /// Byte offset where this file starts in the torrent's linear byte space
    /// </summary>
    public long StartOffset { get; }

    /// <summary>
    /// Index of the first piece that contains data from this file
    /// </summary>
    public int FirstPiece { get; }

    /// <summary>
    /// Index of the last piece that contains data from this file
    /// </summary>
    public int LastPiece { get; }

    /// <summary>
    /// Byte offset within FirstPiece where this file's data starts
    /// </summary>
    public int FirstPieceOffset { get; }

    /// <summary>
    /// Byte offset within LastPiece where this file's data ends (exclusive)
    /// </summary>
    public int LastPieceEndOffset { get; }

    /// <summary>
    /// Bytes completed for this file
    /// </summary>
    public long BytesCompleted { get; set; }

    /// <summary>
    /// Progress as a fraction 0.0 to 1.0
    /// </summary>
    public float Progress => Size > 0 ? (float)BytesCompleted / Size : 0f;

    /// <summary>
    /// Progress as percentage 0-100
    /// </summary>
    public float ProgressPercent => Progress * 100f;

    /// <summary>
    /// Whether this file is completely downloaded
    /// </summary>
    public bool IsComplete => BytesCompleted >= Size;

    /// <summary>
    /// Number of pieces that contain data from this file
    /// </summary>
    public int PieceCount => LastPiece - FirstPiece + 1;

    /// <summary>
    /// File priority for selective download (0 = skip, 1-7 = priority levels)
    /// </summary>
    public int Priority { get; set; } = 4; // Default: Normal

    /// <summary>
    /// Whether this file should be downloaded
    /// </summary>
    public bool IsWanted => Priority > 0;

    /// <summary>
    /// File availability as a fraction 0.0 to 1.0.
    /// Based on minimum availability of pieces in this file.
    /// A value of 1.0+ means at least one complete copy exists in the swarm.
    /// </summary>
    public float Availability { get; set; }

    public FileProgress(
        int fileIndex,
        string path,
        long size,
        long startOffset,
        int firstPiece,
        int lastPiece,
        int firstPieceOffset,
        int lastPieceEndOffset)
    {
        FileIndex = fileIndex;
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Size = size;
        StartOffset = startOffset;
        FirstPiece = firstPiece;
        LastPiece = lastPiece;
        FirstPieceOffset = firstPieceOffset;
        LastPieceEndOffset = lastPieceEndOffset;
    }

    /// <summary>
    /// Check if this file contains data from the specified piece
    /// </summary>
    public bool ContainsPiece(int pieceIndex)
    {
        return pieceIndex >= FirstPiece && pieceIndex <= LastPiece;
    }

    /// <summary>
    /// Get the number of bytes this file contributes to a specific piece
    /// </summary>
    public int GetBytesInPiece(int pieceIndex, int pieceLength)
    {
        if (!ContainsPiece(pieceIndex))
            return 0;

        int start = pieceIndex == FirstPiece ? FirstPieceOffset : 0;
        int end = pieceIndex == LastPiece ? LastPieceEndOffset : pieceLength;

        return end - start;
    }

    /// <summary>
    /// Create a snapshot of this file's progress
    /// </summary>
    public FileProgress CreateSnapshot()
    {
        return new FileProgress(
            FileIndex,
            Path,
            Size,
            StartOffset,
            FirstPiece,
            LastPiece,
            FirstPieceOffset,
            LastPieceEndOffset)
        {
            BytesCompleted = this.BytesCompleted,
            Priority = this.Priority,
            Availability = this.Availability
        };
    }
}
