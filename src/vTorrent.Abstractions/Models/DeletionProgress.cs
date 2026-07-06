using System;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// Phase of a torrent file deletion operation.
/// </summary>
public enum DeletionPhase
{
    DeletingFiles,
    SecureWiping,
    CleaningDirectories
}

/// <summary>
/// Progress report for torrent file deletion operations.
/// </summary>
public record DeletionProgress(
    DeletionPhase Phase,
    string CurrentFile,
    int FileIndex,
    int TotalFiles,
    long BytesProcessed,
    long TotalBytes);
