using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Storage;

namespace vTorrent.Core.PieceIO.Backends;

/// <summary>
/// Composite disk backend that routes each file to either the
/// <see cref="PosixDiskBackend"/> or the <see cref="MmapDiskBackend"/>
/// based on per-file heuristics: file size, network path detection, OS,
/// and (on Windows) virtual-memory pressure.
/// </summary>
internal sealed class AdaptiveDiskBackend : IDiskBackend
{
    // ------------------------------------------------------------------ //
    //  Fields
    // ------------------------------------------------------------------ //

    private readonly PosixDiskBackend _posixBackend;
    private readonly MmapDiskBackend? _mmapBackend;

    /// <summary>Caches the routing decision (Posix or Mmap) per canonical file path.</summary>
    private readonly ConcurrentDictionary<string, DiskBackendType> _routingTable = new(StringComparer.OrdinalIgnoreCase);

    private readonly DiskSettings _settings;
    private readonly ILogger _logger;

    /// <summary>Windows-only: tracks mmap address-space pressure.</summary>
    private readonly VirtualMemoryMonitor? _vmMonitor;

    // ------------------------------------------------------------------ //
    //  Constructor
    // ------------------------------------------------------------------ //

    internal AdaptiveDiskBackend(
        SparseFileManager sparseFileManager,
        IFileLockManager lockManager,
        DiskSettings diskSettings,
        DiskIoMode? writeModeOverride,
        ILogger logger,
        IOptionsMonitor<DiskSettings>? diskMonitor = null,
        DiskAccessHint accessHint = DiskAccessHint.Normal)
    {
        _settings = diskSettings ?? throw new ArgumentNullException(nameof(diskSettings));
        _logger   = logger       ?? throw new ArgumentNullException(nameof(logger));

        _posixBackend = new PosixDiskBackend(
            sparseFileManager, lockManager, diskSettings, writeModeOverride, logger, diskMonitor, accessHint);

        if (!Environment.Is64BitProcess)
        {
            _logger.LogWarning("AdaptiveDiskBackend: 32-bit process detected, mmap backend disabled.");
            _mmapBackend = null;
        }
        else
        {
            _mmapBackend = new MmapDiskBackend(
                sparseFileManager, lockManager, diskSettings, logger, diskMonitor, accessHint);

            if (OperatingSystem.IsWindows())
                _vmMonitor = new VirtualMemoryMonitor(diskSettings.MmapMemoryCeiling);
        }
    }

    // ------------------------------------------------------------------ //
    //  Routing
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Evaluates the routing decision for <paramref name="filePath"/> once
    /// and caches it. Subsequent calls for the same canonical path are O(1).
    /// </summary>
    private DiskBackendType DecideBackend(string filePath)
    {
        // 1. No mmap backend available (32-bit process).
        if (_mmapBackend == null)
            return DiskBackendType.ForcePosix;

        // 2. Network drive → always posix (mmap on network paths is unreliable).
        if (IsNetworkPath(filePath))
            return DiskBackendType.ForcePosix;

        // 3. File too small for mmap to be worthwhile.
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Exists && fileInfo.Length / 16384 <= _settings.MmapFileSizeCutoff)
            return DiskBackendType.ForcePosix;

        // 4. Windows: back off if virtual-address space is under pressure.
        if (_vmMonitor?.IsUnderPressure == true)
            return DiskBackendType.ForcePosix;

        // 5. Platform default: Linux/macOS prefer mmap; Windows prefers posix.
        if (OperatingSystem.IsWindows())
            return DiskBackendType.ForcePosix;

        return DiskBackendType.ForceMmap;
    }

    /// <summary>
    /// Returns the concrete backend to use for <paramref name="filePath"/>,
    /// consulting the routing-decision cache.
    /// </summary>
    private IDiskBackend ResolveBackend(string filePath)
    {
        var type = _routingTable.GetOrAdd(Path.GetFullPath(filePath), DecideBackend);
        return type == DiskBackendType.ForceMmap ? (IDiskBackend)_mmapBackend! : _posixBackend;
    }

    // ------------------------------------------------------------------ //
    //  Network-path detection (inline — StorageDeviceHelper has no API for this)
    // ------------------------------------------------------------------ //

    private static bool IsNetworkPath(string filePath)
    {
        try
        {
            // UNC paths are always remote.
            if (filePath.StartsWith(@"\\", StringComparison.Ordinal) ||
                filePath.StartsWith("//", StringComparison.Ordinal))
                return true;

            if (OperatingSystem.IsWindows())
            {
                string? root = Path.GetPathRoot(filePath);
                if (root is { Length: >= 2 } && root[1] == ':')
                {
                    var driveInfo = new DriveInfo(root);
                    return driveInfo.DriveType == DriveType.Network;
                }
            }
            else
            {
                // POSIX: check /proc/mounts for network filesystem types.
                // Fall back to false (local) when /proc/mounts is unavailable (macOS).
                if (File.Exists("/proc/mounts"))
                {
                    string absPath = Path.GetFullPath(filePath);
                    foreach (string line in File.ReadAllLines("/proc/mounts"))
                    {
                        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 3) continue;
                        string fsType = parts[2];
                        string mountPoint = parts[1];
                        if (!absPath.StartsWith(mountPoint, StringComparison.Ordinal)) continue;
                        // Common network filesystem types.
                        if (fsType is "nfs" or "nfs4" or "cifs" or "smbfs" or "fuse.sshfs" or "davfs")
                            return true;
                    }
                }
            }
        }
        catch
        {
            // Err on the side of caution: if detection fails, assume local.
        }
        return false;
    }

    // ------------------------------------------------------------------ //
    //  IDiskBackend — delegation
    // ------------------------------------------------------------------ //

    /// <inheritdoc/>
    public ValueTask<int> ReadAsync(string filePath, long fileOffset, Memory<byte> buffer, CancellationToken ct = default)
        => ResolveBackend(filePath).ReadAsync(filePath, fileOffset, buffer, ct);

    /// <inheritdoc/>
    public ValueTask WriteAsync(string filePath, long fileOffset, ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => ResolveBackend(filePath).WriteAsync(filePath, fileOffset, buffer, ct);

    /// <inheritdoc/>
    public ValueTask FlushAsync(string filePath, CancellationToken ct = default)
        => ResolveBackend(filePath).FlushAsync(filePath, ct);

    /// <inheritdoc/>
    public ValueTask EnsureAllocatedAsync(string filePath, long requiredSize, CancellationToken ct = default)
        => ResolveBackend(filePath).EnsureAllocatedAsync(filePath, requiredSize, ct);

    /// <inheritdoc/>
    public ValueTask CloseFileAsync(string filePath, CancellationToken ct = default)
        => ResolveBackend(filePath).CloseFileAsync(filePath, ct);

    // ------------------------------------------------------------------ //
    //  IDiskBackend — CloseAllAsync
    // ------------------------------------------------------------------ //

    /// <inheritdoc/>
    public async ValueTask CloseAllAsync(CancellationToken ct = default)
    {
        await _posixBackend.CloseAllAsync(ct).ConfigureAwait(false);

        if (_mmapBackend != null)
            await _mmapBackend.CloseAllAsync(ct).ConfigureAwait(false);

        // Force re-evaluation on next access so re-opened files are re-assessed.
        _routingTable.Clear();
    }

    // ------------------------------------------------------------------ //
    //  IDiskBackend — GetStats
    // ------------------------------------------------------------------ //

    /// <inheritdoc/>
    public DiskBackendStats GetStats()
    {
        var posix = _posixBackend.GetStats();
        var mmap  = _mmapBackend?.GetStats() ?? default;
        return new DiskBackendStats(
            posix.OpenHandleCount   + mmap.OpenHandleCount,
            posix.PendingReads      + mmap.PendingReads,
            posix.PendingWrites     + mmap.PendingWrites,
            posix.TotalBytesRead    + mmap.TotalBytesRead,
            posix.TotalBytesWritten + mmap.TotalBytesWritten);
    }

    // ------------------------------------------------------------------ //
    //  IAsyncDisposable
    // ------------------------------------------------------------------ //

    public async ValueTask DisposeAsync()
    {
        _vmMonitor?.Dispose();
        await _posixBackend.DisposeAsync().ConfigureAwait(false);
        if (_mmapBackend != null)
            await _mmapBackend.DisposeAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ //
    //  VirtualMemoryMonitor — Windows-only inner class
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Periodically polls <see cref="Process.PrivateMemorySize64"/> as a
    /// rough proxy for mmap address-space consumption.  When the value
    /// exceeds <see cref="_ceiling"/> the monitor signals pressure and the
    /// routing logic falls back to the posix backend for new files.
    /// </summary>
    private sealed class VirtualMemoryMonitor : IDisposable
    {
        private long _totalMmapBytes;
        private readonly long _ceiling;
        private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(30));
        private readonly CancellationTokenSource _cts = new();

        public bool IsUnderPressure => Interlocked.Read(ref _totalMmapBytes) > _ceiling;

        public VirtualMemoryMonitor(long ceiling)
        {
            _ceiling = ceiling;
            _ = PollAsync(_cts.Token);
        }

        private async Task PollAsync(CancellationToken ct)
        {
            try
            {
                while (await _timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    // PrivateMemorySize64 is a coarse proxy — it includes all private
                    // pages, not just mmap regions — but it is available everywhere
                    // without platform-specific P/Invoke and is good enough as a
                    // circuit-breaker for address-space exhaustion.
                    long current = Process.GetCurrentProcess().PrivateMemorySize64;
                    Interlocked.Exchange(ref _totalMmapBytes, current);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _timer.Dispose();
        }
    }
}
