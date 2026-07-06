namespace vTorrent.Abstractions.Storage;

/// <summary>
/// Abstraction for disk I/O operations. PieceManager delegates all file I/O to this interface.
/// Implementations manage their own file handle/mapping lifecycle internally.
/// </summary>
public interface IDiskBackend : IAsyncDisposable
{
    /// <summary>Read a segment of a file at the given offset into the buffer.</summary>
    /// <returns>Number of bytes actually read (may be less than buffer.Length at EOF).</returns>
    ValueTask<int> ReadAsync(string filePath, long fileOffset, Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>Write a segment to a file at the given offset from the buffer.</summary>
    ValueTask WriteAsync(string filePath, long fileOffset, ReadOnlyMemory<byte> buffer, CancellationToken ct = default);

    /// <summary>Flush pending writes for a specific file to disk.</summary>
    ValueTask FlushAsync(string filePath, CancellationToken ct = default);

    /// <summary>Ensure the file exists and is allocated (sparse or full) to at least the required size.</summary>
    ValueTask EnsureAllocatedAsync(string filePath, long requiredSize, CancellationToken ct = default);

    /// <summary>Close all handles/mappings for the given file (for move_storage, deletion).</summary>
    ValueTask CloseFileAsync(string filePath, CancellationToken ct = default);

    /// <summary>Close all handles/mappings (for disk fence, shutdown).</summary>
    ValueTask CloseAllAsync(CancellationToken ct = default);

    /// <summary>Backend statistics for monitoring and auto-tuning.</summary>
    DiskBackendStats GetStats();
}

/// <summary>Snapshot of disk backend statistics.</summary>
public readonly record struct DiskBackendStats(
    int OpenHandleCount,
    long PendingReads,
    long PendingWrites,
    long TotalBytesRead,
    long TotalBytesWritten);
