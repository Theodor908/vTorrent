using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Storage;

namespace vTorrent.Core.PieceIO.Backends;

/// <summary>
/// Primary disk I/O backend using .NET's <see cref="RandomAccess"/> API with
/// <see cref="SafeFileHandle"/>s cached in an LRU <see cref="FileHandleCache{THandle}"/>.
/// </summary>
internal sealed class PosixDiskBackend : IDiskBackend
{
    // ------------------------------------------------------------------ //
    //  Fields
    // ------------------------------------------------------------------ //

    private readonly FileHandleCache<SafeFileHandle> _cache;
    private readonly IFileLockManager _lockManager;
    private readonly SparseFileManager _sparseFileManager;
    private readonly DiskIoMode _effectiveWriteMode;
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

    internal PosixDiskBackend(
        SparseFileManager sparseFileManager,
        IFileLockManager lockManager,
        DiskSettings diskSettings,
        DiskIoMode? writeModeOverride,
        ILogger logger,
        IOptionsMonitor<DiskSettings>? diskMonitor = null,
        DiskAccessHint accessHint = DiskAccessHint.Normal)
    {
        _sparseFileManager = sparseFileManager ?? throw new ArgumentNullException(nameof(sparseFileManager));
        _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _diskMonitor = diskMonitor;
        _accessHint = accessHint;

        _effectiveWriteMode = writeModeOverride ?? diskSettings.WriteMode;

        if (_effectiveWriteMode == DiskIoMode.DisableOsCache)
        {
            // True O_DIRECT / FILE_FLAG_NO_BUFFERING requires page-aligned buffers.
            // Fall back to WriteThrough which is supported universally.
            _logger.LogDebug(
                "PosixDiskBackend: DisableOsCache requested but page-aligned I/O is not implemented; " +
                "falling back to WriteThrough (FileOptions.WriteThrough).");
            _effectiveWriteMode = DiskIoMode.WriteThrough;
        }

        // Sentinel -1 means SettingsSeeder did not run yet; treat as disabled.
        var effectiveCloseInterval = diskSettings.CloseFileInterval > 0
            ? diskSettings.CloseFileInterval
            : 0;

        _cache = new FileHandleCache<SafeFileHandle>(
            (path, arg) => OpenFileHandle(path, (FileAccess)arg!),
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
    /// <see cref="RandomAccess"/> is thread-safe for concurrent reads on the same
    /// <see cref="SafeFileHandle"/>, so no per-file lock is acquired here.
    /// </remarks>
    public async ValueTask<int> ReadAsync(
        string filePath,
        long fileOffset,
        Memory<byte> buffer,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _pendingReads);
        try
        {
            var handle = await _cache.AcquireAsync(filePath, FileAccess.Read, FileAccess.Read, ct)
                .ConfigureAwait(false);
            try
            {
                var bytesRead = await RandomAccess.ReadAsync(handle, buffer, fileOffset, ct)
                    .ConfigureAwait(false);
                Interlocked.Add(ref _totalBytesRead, bytesRead);
                return bytesRead;
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
    /// A per-file semaphore serializes allocation + write to prevent concurrent
    /// writers from racing on sparse-file setup.
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
            using var fileLock = await _lockManager.AcquireLockAsync(filePath, ct)
                .ConfigureAwait(false);

            await _sparseFileManager.EnsureFileAllocatedByPathAsync(filePath, ct)
                .ConfigureAwait(false);

            var handle = await _cache.AcquireAsync(filePath, FileAccess.ReadWrite, FileAccess.ReadWrite, ct)
                .ConfigureAwait(false);
            try
            {
                await RandomAccess.WriteAsync(handle, buffer, fileOffset, ct)
                    .ConfigureAwait(false);
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
    /// When <see cref="DiskIoMode.WriteThrough"/> is active the OS already flushes
    /// each write to disk, so this is a no-op.
    /// For <see cref="DiskIoMode.EnableOsCache"/> we issue an OS-level flush via
    /// <c>FlushFileBuffers</c> (Windows) or <c>fsync</c> (POSIX) directly on the
    /// cached <see cref="SafeFileHandle"/>, avoiding any FileStream ownership issues.
    /// The call is offloaded to the thread pool because it may block.
    /// </remarks>
    public async ValueTask FlushAsync(string filePath, CancellationToken ct = default)
    {
        if (_effectiveWriteMode == DiskIoMode.WriteThrough)
            return; // Already flushed on every write.

        // Acquire the cached handle (read-write) so we can fsync it.
        var handle = await _cache.AcquireAsync(filePath, FileAccess.ReadWrite, FileAccess.ReadWrite, ct)
            .ConfigureAwait(false);
        try
        {
            // fsync / FlushFileBuffers may block; run on the thread pool.
            await Task.Run(() => NativeFlush(handle), ct).ConfigureAwait(false);
        }
        finally
        {
            _cache.Release(filePath);
        }
    }

    // ------------------------------------------------------------------ //
    //  Native flush helpers
    // ------------------------------------------------------------------ //

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int FSync(int fd);

    private static void NativeFlush(SafeFileHandle handle)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!FlushFileBuffers(handle))
                throw new IOException(
                    $"FlushFileBuffers failed with Win32 error {Marshal.GetLastWin32Error()}");
        }
        else
        {
            // fsync(2): flush OS page-cache dirty pages and wait for physical write.
            var fd = handle.DangerousGetHandle().ToInt32();
            if (FSync(fd) < 0)
                throw new IOException($"fsync failed with errno {Marshal.GetLastWin32Error()}");
        }
    }

    // ------------------------------------------------------------------ //
    //  IDiskBackend — EnsureAllocatedAsync
    // ------------------------------------------------------------------ //

    /// <inheritdoc/>
    public async ValueTask EnsureAllocatedAsync(string filePath, long requiredSize, CancellationToken ct = default)
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
    //  Private helpers
    // ------------------------------------------------------------------ //

    private SafeFileHandle OpenFileHandle(string filePath, FileAccess access)
    {
        var mode = access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate;
        // Match the existing FileHandlePool pattern: allow external programs to
        // read/write/delete the file while we hold it open.
        const FileShare share = FileShare.ReadWrite | FileShare.Delete;
        var options = FileOptions.Asynchronous;
        if (_accessHint == DiskAccessHint.CheckingMode)
            options |= FileOptions.SequentialScan;
        else
            options |= FileOptions.RandomAccess;

        if (_effectiveWriteMode == DiskIoMode.WriteThrough)
            options |= FileOptions.WriteThrough;

        if (_diskMonitor?.CurrentValue.NoAtimeStorage == true && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // O_NOATIME = 0x40000 — reduces disk wear by skipping access time updates
            options |= (FileOptions)0x40000;
        }

        return File.OpenHandle(filePath, mode, access, share, options);
    }
}
