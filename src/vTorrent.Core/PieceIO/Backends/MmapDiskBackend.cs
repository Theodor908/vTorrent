using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Storage;

namespace vTorrent.Core.PieceIO.Backends;

// ------------------------------------------------------------------ //
//  MmapFileEntry — handle type for FileHandleCache<MmapFileEntry>
// ------------------------------------------------------------------ //

/// <summary>
/// Wraps a <see cref="MemoryMappedFile"/> and its view accessor for use
/// as a cache-managed handle in <see cref="MmapDiskBackend"/>.
/// </summary>
internal sealed class MmapFileEntry : IDisposable
{
    public MemoryMappedFile File { get; }
    public MemoryMappedViewAccessor Accessor { get; }
    public SafeMemoryMappedViewHandle ViewHandle { get; }
    public long MappedSize { get; }

    public MmapFileEntry(MemoryMappedFile mmf, long mappedSize)
    {
        File = mmf ?? throw new ArgumentNullException(nameof(mmf));
        MappedSize = mappedSize;
        Accessor = mmf.CreateViewAccessor(0, mappedSize, MemoryMappedFileAccess.ReadWrite);
        ViewHandle = Accessor.SafeMemoryMappedViewHandle;
    }

    public void Dispose()
    {
        Accessor.Dispose();
        File.Dispose();
    }
}

// ------------------------------------------------------------------ //
//  MmapDiskBackend
// ------------------------------------------------------------------ //

/// <summary>
/// Disk I/O backend using memory-mapped files (<see cref="MemoryMappedFile"/>).
/// Reads and writes are synchronous memory copies wrapped in <see cref="ValueTask"/>,
/// avoiding async state-machine overhead on the hot I/O path.
/// Preferred for files larger than <see cref="DiskSettings.MmapFileSizeCutoff"/> blocks.
/// </summary>
internal sealed class MmapDiskBackend : IDiskBackend
{
    // ------------------------------------------------------------------ //
    //  Fields
    // ------------------------------------------------------------------ //

    private readonly FileHandleCache<MmapFileEntry> _cache;
    private readonly IFileLockManager _lockManager;
    private readonly SparseFileManager _sparseFileManager;
    private readonly ILogger _logger;
    private readonly IOptionsMonitor<DiskSettings>? _diskMonitor;
    private readonly DiskAccessHint _accessHint;
    private readonly IDisposable? _settingsChangeRegistration;

    // Stats — updated via Interlocked
    private long _pendingReads;
    private long _pendingWrites;
    private long _totalBytesRead;
    private long _totalBytesWritten;

    // ------------------------------------------------------------------ //
    //  Constructor
    // ------------------------------------------------------------------ //

    internal MmapDiskBackend(
        SparseFileManager sparseFileManager,
        IFileLockManager lockManager,
        DiskSettings diskSettings,
        ILogger logger,
        IOptionsMonitor<DiskSettings>? diskMonitor = null,
        DiskAccessHint accessHint = DiskAccessHint.Normal)
    {
        _sparseFileManager = sparseFileManager ?? throw new ArgumentNullException(nameof(sparseFileManager));
        _lockManager       = lockManager       ?? throw new ArgumentNullException(nameof(lockManager));
        _logger            = logger            ?? throw new ArgumentNullException(nameof(logger));
        _diskMonitor       = diskMonitor;
        _accessHint        = accessHint;

        // Sentinel -1 means SettingsSeeder did not run yet; treat as disabled.
        var effectiveCloseInterval = diskSettings.CloseFileInterval > 0
            ? diskSettings.CloseFileInterval
            : 0;

        _cache = new FileHandleCache<MmapFileEntry>(
            (path, arg) => CreateMmapEntry(path, (long)arg!),
            maxHandles: diskSettings.FilePoolSize > 0 ? diskSettings.FilePoolSize : 40,
            closeIntervalSeconds: effectiveCloseInterval,
            logger: logger);

        _settingsChangeRegistration = diskMonitor?.OnChange((settings, _) => _cache.UpdateMaxHandles(settings.FilePoolSize));
    }

    // ------------------------------------------------------------------ //
    //  IDiskBackend — ReadAsync
    // ------------------------------------------------------------------ //

    /// <inheritdoc/>
    /// <remarks>
    /// The read is a direct memory copy from the mapped view — no kernel
    /// transition or async I/O is involved.  The method returns
    /// <see cref="ValueTask{TResult}"/> for interface compatibility.
    /// </remarks>
    public ValueTask<int> ReadAsync(
        string filePath,
        long fileOffset,
        Memory<byte> buffer,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pendingReads);
        try
        {
            var requiredEnd = fileOffset + buffer.Length;
            var entry = _cache.Acquire(filePath, FileAccess.Read, requiredEnd);

            try
            {
                // If the file has grown beyond the current mapping we must remap.
                // This is a rare case on the read path; we replace the entry inline.
                if (requiredEnd > entry.MappedSize)
                    entry = RemapEntry(filePath, requiredEnd);

                // Bounds-check before touching the view to avoid AccessViolationException,
                // which cannot be caught on .NET 6+ without corrupted-state exception attributes.
                if (fileOffset < 0 || requiredEnd > entry.MappedSize)
                {
                    _logger.LogWarning(
                        "MmapDiskBackend: read out of range — file={FilePath} offset={Offset} length={Length} mapped={Mapped}",
                        filePath, fileOffset, buffer.Length, entry.MappedSize);
                    return ValueTask.FromResult(0);
                }

                CopyFromView(entry, fileOffset, buffer.Span);
                Interlocked.Add(ref _totalBytesRead, buffer.Length);
                return ValueTask.FromResult(buffer.Length);
            }
            finally
            {
                _cache.Release(filePath);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingReads);
        }
    }

    // ------------------------------------------------------------------ //
    //  IDiskBackend — WriteAsync
    // ------------------------------------------------------------------ //

    /// <inheritdoc/>
    /// <remarks>
    /// A per-file <see cref="IFileLockManager"/> lock serialises sparse allocation
    /// and any remap needed when the write would grow the file beyond the current
    /// mapping.  The actual copy is a synchronous memory write.
    /// </remarks>
    public async ValueTask WriteAsync(
        string filePath,
        long fileOffset,
        ReadOnlyMemory<byte> buffer,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pendingWrites);
        try
        {
            // Async lock acquisition avoids blocking a thread-pool thread.
            using var fileLock = await _lockManager.AcquireLockAsync(filePath, ct)
                .ConfigureAwait(false);

            // Ensure the backing file exists on disk (sparse allocation).
            await _sparseFileManager.EnsureFileAllocatedByPathAsync(filePath, ct)
                .ConfigureAwait(false);

            var requiredSize = fileOffset + buffer.Length;
            var entry = _cache.Acquire(filePath, FileAccess.ReadWrite, requiredSize);

            // Remap if the file has grown beyond the current mapping.
            // The per-file lock is already held, so this is safe.
            if (requiredSize > entry.MappedSize)
            {
                _cache.Release(filePath);
                await _cache.CloseFileAsync(filePath).ConfigureAwait(false);
                entry = _cache.Acquire(filePath, FileAccess.ReadWrite, requiredSize);
            }

            try
            {
                // Bounds-check to prevent AccessViolationException.
                if (fileOffset < 0 || requiredSize > entry.MappedSize)
                {
                    _logger.LogError(
                        "MmapDiskBackend: write out of range — file={FilePath} offset={Offset} length={Length} mapped={Mapped}",
                        filePath, fileOffset, buffer.Length, entry.MappedSize);
                    throw new IOException(
                        $"Mmap write out of range for '{filePath}': offset={fileOffset}, length={buffer.Length}, mapped={entry.MappedSize}");
                }

                CopyToView(entry, fileOffset, buffer.Span);
                Interlocked.Add(ref _totalBytesWritten, buffer.Length);
            }
            finally
            {
                _cache.Release(filePath);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingWrites);
        }
    }

    // ------------------------------------------------------------------ //
    //  IDiskBackend — FlushAsync
    // ------------------------------------------------------------------ //

    /// <inheritdoc/>
    /// <remarks>
    /// Flushes dirty pages in the mapped view to disk via
    /// <see cref="MemoryMappedViewAccessor.Flush"/>.  If no mapping is
    /// currently open for the file this is a no-op.
    /// </remarks>
    public ValueTask FlushAsync(string filePath, CancellationToken ct = default)
    {
        var cacheEntry = _cache.TryGet(filePath);
        if (cacheEntry != null)
            cacheEntry.Handle.Accessor.Flush();

        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ //
    //  IDiskBackend — EnsureAllocatedAsync
    // ------------------------------------------------------------------ //

    /// <inheritdoc/>
    public async ValueTask EnsureAllocatedAsync(
        string filePath,
        long requiredSize,
        CancellationToken ct = default)
    {
        await _sparseFileManager.EnsureFileAllocatedByPathAsync(filePath, ct)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ //
    //  IDiskBackend — CloseFileAsync / CloseAllAsync
    // ------------------------------------------------------------------ //

    /// <inheritdoc/>
    public async ValueTask CloseFileAsync(string filePath, CancellationToken ct = default)
        => await _cache.CloseFileAsync(filePath).ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask CloseAllAsync(CancellationToken ct = default)
        => await _cache.CloseAllAsync(ct).ConfigureAwait(false);

    // ------------------------------------------------------------------ //
    //  IDiskBackend — GetStats
    // ------------------------------------------------------------------ //

    /// <inheritdoc/>
    public DiskBackendStats GetStats() => new(
        _cache.Count,
        Interlocked.Read(ref _pendingReads),
        Interlocked.Read(ref _pendingWrites),
        Interlocked.Read(ref _totalBytesRead),
        Interlocked.Read(ref _totalBytesWritten));

    // ------------------------------------------------------------------ //
    //  IAsyncDisposable
    // ------------------------------------------------------------------ //

    public async ValueTask DisposeAsync()
    {
        _settingsChangeRegistration?.Dispose();
        await _cache.DisposeAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ //
    //  Private helpers — factory, remap, pointer copies
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Factory delegate called by <see cref="FileHandleCache{THandle}"/> when a
    /// new mapping is needed.  <paramref name="requiredSize"/> is the minimum byte
    /// length the caller needs; the mapping is sized to <c>max(fileLength, requiredSize)</c>.
    /// </summary>
    private MmapFileEntry CreateMmapEntry(string filePath, long requiredSize)
    {
        var fileInfo = new FileInfo(filePath);
        var mapSize  = Math.Max(fileInfo.Exists ? fileInfo.Length : 0L, requiredSize);

        // MemoryMappedFile does not accept a zero-length capacity.
        if (mapSize == 0) mapSize = 1;

        var mmf = MemoryMappedFile.CreateFromFile(
            filePath,
            FileMode.OpenOrCreate,
            mapName: null,
            capacity: mapSize,
            MemoryMappedFileAccess.ReadWrite);

        return new MmapFileEntry(mmf, mapSize);
    }

    /// <summary>
    /// Closes the existing cache entry and re-acquires at <paramref name="requiredSize"/>,
    /// forcing the factory to create a larger mapping.  Must be called while the
    /// per-file lock is held (write path) or at entry creation time (read path).
    /// </summary>
    private MmapFileEntry RemapEntry(string filePath, long requiredSize)
    {
        // Release the current ref-count before closing.
        _cache.Release(filePath);
        _cache.CloseFileAsync(filePath).AsTask().GetAwaiter().GetResult();
        return _cache.Acquire(filePath, FileAccess.ReadWrite, requiredSize);
    }

    /// <summary>
    /// Copies <paramref name="source"/> from the mapped view at <paramref name="offset"/>
    /// into <paramref name="destination"/> using an unsafe pointer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void CopyFromView(MmapFileEntry entry, long offset, Span<byte> destination)
    {
        byte* ptr = null;
        entry.ViewHandle.AcquirePointer(ref ptr);
        try
        {
            new ReadOnlySpan<byte>(ptr + offset, destination.Length).CopyTo(destination);
        }
        finally
        {
            entry.ViewHandle.ReleasePointer();
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> into the mapped view at <paramref name="offset"/>
    /// using an unsafe pointer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void CopyToView(MmapFileEntry entry, long offset, ReadOnlySpan<byte> source)
    {
        byte* ptr = null;
        entry.ViewHandle.AcquirePointer(ref ptr);
        try
        {
            source.CopyTo(new Span<byte>(ptr + offset, source.Length));
        }
        finally
        {
            entry.ViewHandle.ReleasePointer();
        }
    }
}
