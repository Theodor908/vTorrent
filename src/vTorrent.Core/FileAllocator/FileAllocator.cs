using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Core.FileAllocator
{
    public class FileAllocator : IFileAllocator
    {
        private readonly int BufferSize = 1024 * 1024;

        public FileAllocator() { }

        public FileAllocator(int bufferSize)
        {
            BufferSize = bufferSize;
        }

        public AllocationResult AllocateFile(string filePath, long size, AllocationStrategy strategy)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return AllocationResult.Failure(AllocationError.PathInvalid, "File path cannot be null or empty");
            }

            if (size <= 0)
            {
                return AllocationResult.Failure(AllocationError.PathInvalid, "File size must be greater than zero");
            }

            var stopwatch = Stopwatch.StartNew();
            var allocatedFiles = new List<string>();

            try
            {
                filePath = Path.GetFullPath(filePath);

                if (!CheckDiskSpace(filePath, size, out long available))
                {
                    return AllocationResult.Failure(
                        AllocationError.InsufficientSpace,
                        $"Insufficient disk space. Required: {size} bytes, Available: {available} bytes",
                        size,
                        available);
                }

                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    try
                    {
                        Directory.CreateDirectory(directory);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return AllocationResult.Failure(
                            AllocationError.PermissionDenied,
                            $"Permission denied creating directory: {directory}");
                    }
                    catch (Exception ex)
                    {
                        return AllocationResult.Failure(
                            AllocationError.DirectoryCreationFailed,
                            $"Failed to create directory: {ex.Message}");
                    }
                }

                if (strategy == AllocationStrategy.Sparse)
                {
                    AllocateSparse(filePath, size);
                }
                else if (strategy == AllocationStrategy.Full)
                {
                    AllocateFull(filePath, size);
                }

                allocatedFiles.Add(filePath);
                stopwatch.Stop();

                return AllocationResult.Success(allocatedFiles, size, strategy, stopwatch.Elapsed);

            }
            catch (UnauthorizedAccessException)
            {
                return AllocationResult.Failure(
                    AllocationError.PermissionDenied,
                    $"Permission denied accessing file: {filePath}");
            }
            catch (IOException ex)
            {
                return AllocationResult.Failure(
                    AllocationError.FileCreationFailed,
                    $"I/O error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return AllocationResult.Failure(
                    AllocationError.AllocationFailed,
                    $"Unexpected error: {ex.Message}");
            }

        }

        public async Task<AllocationResult> AllocateFileAsync(string filePath, long size, AllocationStrategy strategy, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return AllocationResult.Failure(AllocationError.PathInvalid, "File path cannot be null or empty");

            if (size <= 0)
                return AllocationResult.Failure(AllocationError.PathInvalid, "File size must be greater than zero");

            var stopwatch = Stopwatch.StartNew();
            var allocatedFiles = new List<string>();

            try
            {
                filePath = Path.GetFullPath(filePath);

                // Check disk space
                if (!CheckDiskSpace(filePath, size, out long available))
                {
                    return AllocationResult.Failure(
                        AllocationError.InsufficientSpace,
                        $"Insufficient disk space. Required: {size} bytes, Available: {available} bytes",
                        size,
                        available);
                }

                // Create directory
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Allocate
                if (strategy == AllocationStrategy.Sparse)
                {
                    await AllocateSparseAsync(filePath, size, cancellationToken);
                }
                else if (strategy == AllocationStrategy.Full)
                {
                    await AllocateFullAsync(filePath, size, null, cancellationToken);
                }

                allocatedFiles.Add(filePath);
                stopwatch.Stop();

                return AllocationResult.Success(
                    allocatedFiles,
                    size,
                    strategy,
                    stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                // Clean up partial allocation
                if (File.Exists(filePath))
                {
                    try
                    {
                        File.Delete(filePath);
                    }
                    catch (IOException ex)
                    {
                        // File may be in use or locked, log but continue with cleanup
                        System.Diagnostics.Debug.WriteLine($"Failed to delete partially allocated file {filePath}: {ex.Message}");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Permission denied deleting file {filePath}: {ex.Message}");
                    }
                }
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                return AllocationResult.Failure(
                    AllocationError.PermissionDenied,
                    $"Permission denied accessing file: {filePath}");
            }
            catch (IOException ex)
            {
                return AllocationResult.Failure(
                    AllocationError.FileCreationFailed,
                    $"I/O error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return AllocationResult.Failure(
                    AllocationError.AllocationFailed,
                    $"Unexpected error: {ex.Message}");
            }
        }

        public AllocationResult AllocateFiles(string basePath, TorrentInfo torrentInfo, AllocationStrategy strategy)
        {
            if (string.IsNullOrWhiteSpace(basePath))
                return AllocationResult.Failure(AllocationError.PathInvalid, "Base path cannot be null or empty");

            if (torrentInfo == null)
                return AllocationResult.Failure(AllocationError.PathInvalid, "TorrentInfo cannot be null");

            var stopwatch = Stopwatch.StartNew();
            var allocatedFiles = new List<string>();

            try
            {
                basePath = Path.GetFullPath(basePath);

                // Check FileMode to properly distinguish single vs multi-file torrents
                var isSingleFile = torrentInfo.FileMode == Bencode.Torrents.TorrentFileMode.Single;

                if (isSingleFile)
                {
                    // Single-file torrent: file is named directly in base path
                    var filePath = Path.Combine(basePath, torrentInfo.Name);
                    return AllocateFile(filePath, torrentInfo.TotalSize, strategy);
                }

                // Multi-file torrent: create a directory named after the torrent
                // and place all files inside (libtorrent standard behavior)
                var torrentDir = Path.Combine(basePath, torrentInfo.Name);
                var totalSize = torrentInfo.TotalSize;

                // Check disk space
                if (!CheckDiskSpace(basePath, totalSize, out long available))
                {
                    return AllocationResult.Failure(
                        AllocationError.InsufficientSpace,
                        $"Insufficient disk space. Required: {totalSize} bytes, Available: {available} bytes",
                        totalSize,
                        available);
                }

                // Create torrent directory
                if (!Directory.Exists(torrentDir))
                {
                    Directory.CreateDirectory(torrentDir);
                }

                // Allocate each file
                foreach (var file in torrentInfo.Files)
                {
                    var filePath = BuildFilePath(torrentDir, file.Path);

                    // Create directory structure
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Allocate file
                    if (strategy != AllocationStrategy.None)
                    {
                        if (strategy == AllocationStrategy.Sparse)
                        {
                            AllocateSparse(filePath, file.Length);
                        }
                        else if (strategy == AllocationStrategy.Full)
                        {
                            AllocateFull(filePath, file.Length);
                        }
                    }

                    allocatedFiles.Add(filePath);
                }

                stopwatch.Stop();

                return AllocationResult.Success(
                    allocatedFiles,
                    totalSize,
                    strategy,
                    stopwatch.Elapsed);
            }
            catch (UnauthorizedAccessException ex)
            {
                return AllocationResult.Failure(
                    AllocationError.PermissionDenied,
                    $"Permission denied: {ex.Message}");
            }
            catch (IOException ex)
            {
                return AllocationResult.Failure(
                    AllocationError.FileCreationFailed,
                    $"I/O error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return AllocationResult.Failure(
                    AllocationError.AllocationFailed,
                    $"Unexpected error: {ex.Message}");
            }
        }

        public async Task<AllocationResult> AllocateFilesAsync(string basePath, TorrentInfo torrentInfo, AllocationStrategy strategy, IProgress<AllocationProgress> progress = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(basePath))
                return AllocationResult.Failure(AllocationError.PathInvalid, "Base path cannot be null or empty");

            if (torrentInfo == null)
                return AllocationResult.Failure(AllocationError.PathInvalid, "TorrentInfo cannot be null");

            var stopwatch = Stopwatch.StartNew();
            var allocatedFiles = new List<string>();

            try
            {
                basePath = Path.GetFullPath(basePath);

                // Check FileMode to properly distinguish single vs multi-file torrents
                var isSingleFile = torrentInfo.FileMode == Bencode.Torrents.TorrentFileMode.Single;

                if (isSingleFile)
                {
                    // Single-file torrent: file is named directly in base path
                    var filePath = Path.Combine(basePath, torrentInfo.Name);
                    return await AllocateFileAsync(filePath, torrentInfo.TotalSize, strategy, cancellationToken);
                }

                // Multi-file torrent: create a directory named after the torrent
                // and place all files inside (libtorrent standard behavior)
                var torrentDir = Path.Combine(basePath, torrentInfo.Name);
                var totalSize = torrentInfo.TotalSize;

                // Check disk space
                if (!CheckDiskSpace(basePath, totalSize, out long available))
                {
                    return AllocationResult.Failure(
                        AllocationError.InsufficientSpace,
                        $"Insufficient disk space. Required: {totalSize} bytes, Available: {available} bytes",
                        totalSize,
                        available);
                }

                // Create torrent directory
                if (!Directory.Exists(torrentDir))
                {
                    Directory.CreateDirectory(torrentDir);
                }

                // Allocate each file
                long bytesAllocated = 0;
                for (int i = 0; i < torrentInfo.Files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var file = torrentInfo.Files[i];
                    var filePath = BuildFilePath(torrentDir, file.Path);

                    // Report progress
                    progress?.Report(new AllocationProgress
                    {
                        CurrentFileIndex = i,
                        TotalFiles = torrentInfo.Files.Count,
                        BytesAllocated = bytesAllocated,
                        TotalBytes = totalSize,
                        CurrentFileName = filePath
                    });

                    // Create directory structure
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Allocate file
                    if (strategy != AllocationStrategy.None)
                    {
                        if (strategy == AllocationStrategy.Sparse)
                        {
                            await AllocateSparseAsync(filePath, file.Length, cancellationToken);
                        }
                        else if (strategy == AllocationStrategy.Full)
                        {
                            await AllocateFullAsync(filePath, file.Length, progress, cancellationToken);
                        }
                    }

                    allocatedFiles.Add(filePath);
                    bytesAllocated += file.Length;
                }

                // Final progress report
                progress?.Report(new AllocationProgress
                {
                    CurrentFileIndex = torrentInfo.Files.Count,
                    TotalFiles = torrentInfo.Files.Count,
                    BytesAllocated = totalSize,
                    TotalBytes = totalSize,
                    CurrentFileName = "Complete"
                });

                stopwatch.Stop();

                return AllocationResult.Success(
                    allocatedFiles,
                    totalSize,
                    strategy,
                    stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                // Clean up partially allocated files
                foreach (var file in allocatedFiles)
                {
                    try
                    {
                        if (File.Exists(file))
                            File.Delete(file);
                    }
                    catch (IOException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to cleanup file {file} after cancellation: {ex.Message}");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Permission denied cleaning up file {file}: {ex.Message}");
                    }
                }
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                return AllocationResult.Failure(
                    AllocationError.PermissionDenied,
                    $"Permission denied: {ex.Message}");
            }
            catch (IOException ex)
            {
                return AllocationResult.Failure(
                    AllocationError.FileCreationFailed,
                    $"I/O error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return AllocationResult.Failure(
                    AllocationError.AllocationFailed,
                    $"Unexpected error: {ex.Message}");
            }
        }

        public AllocationStatus CheckAllocation(string basePath, TorrentInfo torrentInfo)
        {
            if (string.IsNullOrWhiteSpace(basePath) || torrentInfo == null)
            {
                return new AllocationStatus
                {
                    AllFilesExist = false,
                    AllSizesCorrect = false,
                    MissingFiles = new List<string>(),
                    IncorrectSizeFiles = new List<string>()
                };
            }

            basePath = Path.GetFullPath(basePath);

            var missingFiles = new List<string>();
            var incorrectSizeFiles = new List<string>();

            // Check FileMode to properly distinguish single vs multi-file torrents
            var isSingleFile = torrentInfo.FileMode == Bencode.Torrents.TorrentFileMode.Single;

            if (isSingleFile)
            {
                // Single-file torrent: file is named directly in base path
                var filePath = Path.Combine(basePath, torrentInfo.Name);

                if (!File.Exists(filePath))
                {
                    missingFiles.Add(filePath);
                }
                else
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Length != torrentInfo.TotalSize)
                    {
                        incorrectSizeFiles.Add(filePath);
                    }
                }
            }
            else
            {
                // Multi-file torrent: files are in a directory named after the torrent
                var torrentDir = Path.Combine(basePath, torrentInfo.Name);

                foreach (var file in torrentInfo.Files)
                {
                    var filePath = BuildFilePath(torrentDir, file.Path);

                    if (!File.Exists(filePath))
                    {
                        missingFiles.Add(filePath);
                    }
                    else
                    {
                        var fileInfo = new FileInfo(filePath);
                        if (fileInfo.Length != file.Length)
                        {
                            incorrectSizeFiles.Add(filePath);
                        }
                    }
                }
            }

            return new AllocationStatus
            {
                AllFilesExist = missingFiles.Count == 0,
                AllSizesCorrect = incorrectSizeFiles.Count == 0,
                MissingFiles = missingFiles,
                IncorrectSizeFiles = incorrectSizeFiles
            };
        }

        private bool CheckDiskSpace(string path, long requiredSize, out long availableSpace)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var root = Path.GetPathRoot(fullPath);
                var driveInfo = new DriveInfo(root);
                availableSpace = driveInfo.AvailableFreeSpace;

                // Subtract existing file size — no need to re-allocate space the file already occupies
                long existingSize = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
                long additionalNeeded = Math.Max(0, requiredSize - existingSize);
                return availableSpace >= additionalNeeded;
            }
            catch (ArgumentException)
            {
                // Invalid path format
                availableSpace = 0;
                return false;
            }
            catch (IOException)
            {
                // Drive not found or not accessible
                availableSpace = 0;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // Permission denied to access drive info
                availableSpace = 0;
                return false;
            }
        }

        private void AllocateSparse(string path, long size)
        {
            // Use full sharing to allow external programs to access files during allocation
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.SetLength(size);
            }
        }

        private async Task AllocateSparseAsync(string path, long size, CancellationToken cancellationToken = default)
        {
            // Use full sharing to allow external programs to access files during allocation
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
            {
                fs.SetLength(size);
                await fs.FlushAsync(cancellationToken);
            }
        }

        private void AllocateFull(string path, long size)
        {
            // Use full sharing to allow external programs to access files during allocation
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.SetLength(size);

                var buffer = new byte[BufferSize];
                long remaining = size;

                while (remaining > 0)
                {
                    int toWrite = (int)Math.Min(buffer.Length, remaining);
                    fs.Write(buffer, 0, toWrite);
                    remaining -= toWrite;
                }

                fs.Flush(flushToDisk: true);
            }
        }

        private async Task AllocateFullAsync(string path, long size, IProgress<AllocationProgress> progress, CancellationToken cancellationToken)
        {
            // Use full sharing to allow external programs to access files during allocation
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true))
            {
                fs.SetLength(size);

                var buffer = new byte[BufferSize];
                long remaining = size;
                long written = 0;

                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int toWrite = (int)Math.Min(buffer.Length, remaining);
                    await fs.WriteAsync(buffer, 0, toWrite, cancellationToken);

                    written += toWrite;
                    remaining -= toWrite;
                }

                await fs.FlushAsync(cancellationToken);
            }

        }

        private string BuildFilePath(string basePath, IReadOnlyList<string> pathComponents)
        {
            if (pathComponents == null || pathComponents.Count == 0)
                throw new ArgumentException("Path components cannot be null or empty");

            var parts = new string[pathComponents.Count + 1];
            parts[0] = basePath;

            for(int i = 0; i < pathComponents.Count; i++)
            {
                parts[i + 1] = pathComponents[i];
            }

            return Path.Combine(parts);
        }

    }
}
