using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Bencode.Torrents;

namespace vTorrent.Core.PieceIO;

/// <summary>
/// Manages sparse file creation following libtorrent's lazy allocation pattern.
/// Files are only allocated on disk when data is actually written to them.
///
/// Cross-platform sparse file support:
/// - Windows (NTFS): Requires explicit FSCTL_SET_SPARSE ioctl call, otherwise SetLength() allocates full space
/// - Linux (ext4, btrfs, xfs): Sparse files are automatic - SetLength() creates sparse regions by default
/// - macOS (APFS, HFS+): Sparse files are automatic - SetLength() creates sparse regions by default
///
/// Platform-specific sparse file setup is abstracted away here.
/// </summary>
public class SparseFileManager : IDisposable
{
    // Windows-specific: DeviceIoControl for sparse files
    // On Linux/macOS, sparse files are created automatically by the file system
    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    // Cache the platform check for performance
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private readonly string _basePath;
    private readonly TorrentInfo _torrentInfo;
    private readonly ConcurrentDictionary<int, FileAllocationInfo> _fileInfo;
    private readonly ConcurrentDictionary<string, int> _pathToIndex;  // Maps file path to file index
    private readonly BitArray _fileCreated;  // Tracks which files have been allocated
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Information about a file's allocation state
    /// </summary>
    private class FileAllocationInfo
    {
        public string FilePath { get; init; }
        public long FileSize { get; init; }
        public bool IsCreated { get; set; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    public SparseFileManager(string basePath, TorrentInfo torrentInfo)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(torrentInfo));

        var fileCount = torrentInfo.Files?.Count ?? 1;
        _fileInfo = new ConcurrentDictionary<int, FileAllocationInfo>();
        _pathToIndex = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _fileCreated = new BitArray(fileCount);

        InitializeFileInfo();
    }

    private void InitializeFileInfo()
    {
        // Check FileMode to properly distinguish single vs multi-file torrents
        var isSingleFile = _torrentInfo.FileMode == Bencode.Torrents.TorrentFileMode.Single;

        if (isSingleFile)
        {
            // Single file torrent: file is named directly in base path
            var filePath = Path.Combine(_basePath, _torrentInfo.Name);
            var normalizedPath = Path.GetFullPath(filePath);
            _fileInfo[0] = new FileAllocationInfo
            {
                FilePath = normalizedPath,
                FileSize = _torrentInfo.TotalSize,
                IsCreated = File.Exists(normalizedPath) && new FileInfo(normalizedPath).Length == _torrentInfo.TotalSize
            };
            _fileCreated[0] = _fileInfo[0].IsCreated;
            _pathToIndex[normalizedPath] = 0;
        }
        else
        {
            // Multi-file torrent: files are in a directory named after the torrent
            var torrentDir = Path.Combine(_basePath, _torrentInfo.Name);
            for (int i = 0; i < _torrentInfo.Files.Count; i++)
            {
                var file = _torrentInfo.Files[i];
                var filePath = BuildFilePath(torrentDir, file.Path);
                var normalizedPath = Path.GetFullPath(filePath);
                var exists = File.Exists(normalizedPath);
                var correctSize = exists && new FileInfo(normalizedPath).Length == file.Length;

                _fileInfo[i] = new FileAllocationInfo
                {
                    FilePath = normalizedPath,
                    FileSize = file.Length,
                    IsCreated = correctSize
                };
                _fileCreated[i] = correctSize;
                _pathToIndex[normalizedPath] = i;
            }
        }
    }

    /// <summary>
    /// Ensures a file is created with the correct size before writing.
    /// Uses sparse file allocation on Windows for efficient disk usage.
    /// Only allocates on first write - subsequent writes skip allocation.
    /// </summary>
    public async Task EnsureFileAllocatedAsync(int fileIndex, CancellationToken cancellationToken = default)
    {
        if (!_fileInfo.TryGetValue(fileIndex, out var info))
            throw new ArgumentOutOfRangeException(nameof(fileIndex));

        // Fast path: already created
        if (info.IsCreated)
            return;

        await info.Lock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (info.IsCreated)
                return;

            await CreateSparseFileAsync(info.FilePath, info.FileSize, cancellationToken);
            info.IsCreated = true;

            lock (_lock)
            {
                _fileCreated[fileIndex] = true;
            }
        }
        finally
        {
            info.Lock.Release();
        }
    }

    /// <summary>
    /// Ensures a file is created (synchronous version).
    /// </summary>
    public void EnsureFileAllocated(int fileIndex)
    {
        if (!_fileInfo.TryGetValue(fileIndex, out var info))
            throw new ArgumentOutOfRangeException(nameof(fileIndex));

        // Fast path: already created
        if (info.IsCreated)
            return;

        // Use timeout to prevent indefinite blocking - 30 seconds for file allocation
        if (!info.Lock.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException($"Timed out waiting for file allocation lock on file index: {fileIndex}");
        }
        try
        {
            // Double-check after acquiring lock
            if (info.IsCreated)
                return;

            CreateSparseFile(info.FilePath, info.FileSize);
            info.IsCreated = true;

            lock (_lock)
            {
                _fileCreated[fileIndex] = true;
            }
        }
        finally
        {
            info.Lock.Release();
        }
    }

    /// <summary>
    /// Gets whether a file has been allocated.
    /// </summary>
    public bool IsFileCreated(int fileIndex)
    {
        lock (_lock)
        {
            if (fileIndex < 0 || fileIndex >= _fileCreated.Length)
                return false;
            return _fileCreated[fileIndex];
        }
    }

    /// <summary>
    /// Ensures a file is allocated by path. Looks up the file index internally.
    /// </summary>
    public async Task EnsureFileAllocatedByPathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (_pathToIndex.TryGetValue(normalizedPath, out var fileIndex))
        {
            await EnsureFileAllocatedAsync(fileIndex, cancellationToken);
        }
        // If path not found in our index, the file might be a single-file torrent
        // or already exists - caller handles file operations
    }

    /// <summary>
    /// Ensures a file is allocated by path (synchronous version).
    /// </summary>
    public void EnsureFileAllocatedByPath(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (_pathToIndex.TryGetValue(normalizedPath, out var fileIndex))
        {
            EnsureFileAllocated(fileIndex);
        }
    }

    /// <summary>
    /// Checks if a file (by path) has been allocated.
    /// </summary>
    public bool IsFileCreatedByPath(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (_pathToIndex.TryGetValue(normalizedPath, out var fileIndex))
        {
            return IsFileCreated(fileIndex);
        }
        // If not in our index, assume it exists
        return File.Exists(normalizedPath);
    }

    /// <summary>
    /// Gets the file path for a file index.
    /// </summary>
    public string GetFilePath(int fileIndex)
    {
        if (!_fileInfo.TryGetValue(fileIndex, out var info))
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        return info.FilePath;
    }

    /// <summary>
    /// Creates a sparse file with the specified size.
    /// On Windows, uses FSCTL_SET_SPARSE for true sparse file support.
    /// </summary>
    private async Task CreateSparseFileAsync(string filePath, long size, CancellationToken cancellationToken)
    {
        // Ensure directory exists
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Create the file with full sharing (like libtorrent)
        // This allows external programs to access the file while we're allocating it
        using var fs = new FileStream(
            filePath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous);

        // Set sparse attribute on Windows (Linux/macOS handle this automatically)
        if (IsWindows)
        {
            SetSparseAttribute(fs.SafeFileHandle.DangerousGetHandle());
        }

        // Set the file length
        // - On Windows with sparse attribute: only sets logical size, physical space allocated on write
        // - On Linux/macOS: file systems handle sparse regions automatically
        fs.SetLength(size);
        await fs.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Creates a sparse file synchronously.
    /// </summary>
    private void CreateSparseFile(string filePath, long size)
    {
        // Ensure directory exists
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Create the file with full sharing (like libtorrent)
        // This allows external programs to access the file while we're allocating it
        using var fs = new FileStream(
            filePath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        // Set sparse attribute on Windows (Linux/macOS handle this automatically)
        if (IsWindows)
        {
            SetSparseAttribute(fs.SafeFileHandle.DangerousGetHandle());
        }

        // Set the file length
        fs.SetLength(size);
        fs.Flush();
    }

    /// <summary>
    /// Sets the SPARSE attribute on a file handle.
    /// Windows-only: NTFS requires this flag to enable sparse file behavior.
    /// On Linux/macOS this is a no-op since those file systems handle sparse files automatically.
    /// </summary>
    private static void SetSparseAttribute(IntPtr fileHandle)
    {
        // Only called on Windows, but guard anyway for safety
        if (!IsWindows)
            return;

        DeviceIoControl(
            fileHandle,
            FSCTL_SET_SPARSE,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);
        // Ignore errors - sparse files are an optimization, not a requirement
        // Some file systems (FAT32, exFAT) don't support sparse files
    }

    /// <summary>
    /// Initialize directory structure without allocating files.
    /// Creates directories and empty (0-byte) placeholder files.
    /// </summary>
    public void InitializeDirectoryStructure()
    {
        foreach (var kvp in _fileInfo)
        {
            var info = kvp.Value;
            var directory = Path.GetDirectoryName(info.FilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }

    /// <summary>
    /// Check if sufficient disk space is available for the remaining unallocated files.
    /// Unlike full pre-allocation, this only checks for space needed for files
    /// that haven't been created yet.
    /// </summary>
    public bool HasSufficientDiskSpace(out long requiredBytes, out long availableBytes)
    {
        requiredBytes = 0;
        foreach (var kvp in _fileInfo)
        {
            if (!kvp.Value.IsCreated)
            {
                requiredBytes += kvp.Value.FileSize;
            }
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_basePath));
            var driveInfo = new DriveInfo(root);
            availableBytes = driveInfo.AvailableFreeSpace;
            return availableBytes >= requiredBytes;
        }
        catch
        {
            availableBytes = 0;
            return false;
        }
    }

    private static string BuildFilePath(string basePath, System.Collections.Generic.IReadOnlyList<string> pathComponents)
    {
        if (pathComponents == null || pathComponents.Count == 0)
            throw new ArgumentException("Path components cannot be null or empty");

        var parts = new string[pathComponents.Count + 1];
        parts[0] = basePath;

        for (int i = 0; i < pathComponents.Count; i++)
        {
            parts[i + 1] = pathComponents[i];
        }

        return Path.Combine(parts);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var kvp in _fileInfo)
        {
            kvp.Value.Lock?.Dispose();
        }
    }
}
