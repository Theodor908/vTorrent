using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using vTorrent.Bencode.Torrents;

using vTorrent.Core.FileAllocator;

using vTorrent.Core.PeerCommunication.Utilities;

using vTorrent.Core.PieceIO;

using vTorrent.Core.ResumeData;

using vTorrent.Core.Session;

using vTorrent.Abstractions.Events;

namespace vTorrent.Core.Engine;

/// <summary>
/// Manages file operations for a torrent: allocation, moving, copying,
/// integrity verification, and file path resolution.
/// Extracted from TorrentEngine as part of god class decomposition (Phase 5).
/// </summary>
internal class EngineFileManager
{
    private readonly Torrent _torrent;
    private readonly IPieceManager _pieceManager;
    private readonly ILogger _logger;
    private readonly Func<string> _getDownloadPath;
    private readonly Func<Bitfield> _getLocalBitfield;
    private readonly Func<IResumeDataProvider?> _getResumeDataProvider;

    internal EngineFileManager(
        Torrent torrent,
        IPieceManager pieceManager,
        ILogger logger,
        Func<string> getDownloadPath,
        Func<Bitfield> getLocalBitfield,
        Func<IResumeDataProvider?> getResumeDataProvider)
    {
        _torrent = torrent ?? throw new ArgumentNullException(nameof(torrent));
        _pieceManager = pieceManager ?? throw new ArgumentNullException(nameof(pieceManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _getDownloadPath = getDownloadPath ?? throw new ArgumentNullException(nameof(getDownloadPath));
        _getLocalBitfield = getLocalBitfield ?? throw new ArgumentNullException(nameof(getLocalBitfield));
        _getResumeDataProvider = getResumeDataProvider ?? throw new ArgumentNullException(nameof(getResumeDataProvider));
    }

    /// <summary>
    /// Allocates files for the torrent using lazy allocation strategy.
    /// </summary>
    internal async Task<IFileAllocator> AllocateFilesAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Preparing file structure (lazy allocation)...");

        var fileAllocator = new FileAllocator.FileAllocator();

        var result = await fileAllocator.AllocateFilesAsync(
            _getDownloadPath(),
            _torrent.Info,
            AllocationStrategy.None,  // Lazy allocation - files allocated on first write
            null,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to prepare file structure: {result.ErrorMessage}");
        }

        _logger.LogDebug("Directory structure prepared for {Count} files",
            result.AllocatedFilePaths.Count);

        return fileAllocator;
    }

    /// <summary>
    /// Internal file move implementation.
    /// </summary>
    internal async Task<(bool success, string error, bool needsRecheck)> MoveFilesInternalAsync(
        string oldPath, string newPath, CancellationToken ct)
    {
        try
        {
            // Ensure destination directory exists
            Directory.CreateDirectory(newPath);

            var torrentName = _torrent.DisplayName;
            var isSingleFile = _torrent.Info.FileMode == TorrentFileMode.Single;

            if (isSingleFile)
            {
                // Single-file torrent: file is named after torrent directly in base path
                var oldFilePath = Path.Combine(oldPath, torrentName);
                var newFilePath = Path.Combine(newPath, torrentName);

                if (File.Exists(oldFilePath))
                {
                    // Try fast move (same volume)
                    try
                    {
                        File.Move(oldFilePath, newFilePath);
                        return (true, null, false);
                    }
                    catch (IOException)
                    {
                        // Cross-volume move - copy and delete
                        _logger.LogDebug("Cross-volume move detected for single file, using copy+delete");
                        await CopyFileAsync(oldFilePath, newFilePath, ct).ConfigureAwait(false);
                        File.Delete(oldFilePath);
                        return (true, null, true);
                    }
                }
                else
                {
                    // No source found - might be a fresh torrent, just update path
                    _logger.LogDebug("Source file not found at {OldPath}, assuming fresh torrent", oldFilePath);
                    return (true, null, false);
                }
            }
            else
            {
                // Multi-file torrent: files are in a directory named after the torrent
                var oldTorrentDir = Path.Combine(oldPath, torrentName);
                var newTorrentDir = Path.Combine(newPath, torrentName);

                if (Directory.Exists(oldTorrentDir))
                {
                    // Try fast move (same volume)
                    try
                    {
                        Directory.Move(oldTorrentDir, newTorrentDir);
                        return (true, null, false);
                    }
                    catch (IOException)
                    {
                        // Cross-volume move - copy and delete
                        _logger.LogDebug("Cross-volume move detected, using copy+delete");
                        await CopyDirectoryAsync(oldTorrentDir, newTorrentDir, ct).ConfigureAwait(false);
                        Directory.Delete(oldTorrentDir, recursive: true);
                        return (true, null, true);
                    }
                }
                else
                {
                    // No source found - might be a fresh torrent, just update path
                    _logger.LogDebug("Source directory not found at {OldPath}, assuming fresh torrent", oldTorrentDir);
                    return (true, null, false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move files");
            return (false, ex.Message, false);
        }
    }

    /// <summary>
    /// Copies a directory recursively with async I/O.
    /// </summary>
    internal async Task CopyDirectoryAsync(string sourceDir, string destDir, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            await CopyFileAsync(file, destFile, ct).ConfigureAwait(false);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            await CopyDirectoryAsync(dir, destSubDir, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Copies a file with async I/O.
    /// </summary>
    internal async Task CopyFileAsync(string source, string dest, CancellationToken ct)
    {
        const int bufferSize = 81920; // 80KB buffer
        using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true);
        using var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, true);
        await sourceStream.CopyToAsync(destStream, bufferSize, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Get all file paths for this torrent.
    /// </summary>
    internal IEnumerable<string> GetTorrentFilePaths()
    {
        var downloadPath = _getDownloadPath();

        if (_torrent.Info.Files.Count == 1)
        {
            // Single file torrent
            yield return Path.Combine(downloadPath, _torrent.Info.Files[0].GetFullPath());
        }
        else
        {
            // Multi-file torrent
            foreach (var file in _torrent.Info.Files)
            {
                yield return Path.Combine(downloadPath, _torrent.DisplayName, file.GetFullPath());
            }
        }
    }

    /// <summary>
    /// Returns true if files were modified and full verification is needed.
    /// </summary>
    internal async Task<bool> CheckFilesModifiedAsync(CancellationToken ct)
    {
        var resumeDataProvider = _getResumeDataProvider();
        if (resumeDataProvider == null)
            return true;

        try
        {
            var lastActive = await resumeDataProvider.GetLastActiveTimeAsync().ConfigureAwait(false);

            if (lastActive == DateTime.MinValue)
            {
                _logger.LogDebug("No last active time available, assuming files may be modified");
                return true;
            }

            foreach (var filePath in GetTorrentFilePaths())
            {
                if (!File.Exists(filePath))
                {
                    // File doesn't exist - might be partial download, not necessarily modified
                    continue;
                }

                var fileInfo = new FileInfo(filePath);

                if (fileInfo.LastWriteTimeUtc > lastActive.AddSeconds(30)) // 30 second tolerance
                {
                    _logger.LogDebug("File {Path} modified since last save (file: {FileTime}, saved: {SaveTime})",
                        filePath, fileInfo.LastWriteTimeUtc, lastActive);
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking file modification times");
            return true; // Assume modified on error, do full verification
        }
    }

    /// <summary>
    /// Determines whether to do quick or full verification based on file modification times.
    /// </summary>
    internal async Task<VerificationMode> DetermineVerificationModeAsync()
    {
        var resumeDataProvider = _getResumeDataProvider();
        if (resumeDataProvider == null)
        {
            _logger.LogDebug("No resume data provider, using full verification");
            return VerificationMode.Full;
        }

        try
        {
            // Check if files were modified since last session
            var lastActive = await resumeDataProvider.GetLastActiveTimeAsync().ConfigureAwait(false);
            var pauseDuration = DateTime.UtcNow - lastActive;

            // Optimization: If paused for < 5 minutes, skip verification entirely
            if (pauseDuration < TimeSpan.FromMinutes(5))
            {
                _logger.LogDebug("Short pause duration ({Duration}), skipping verification", pauseDuration);
                return VerificationMode.QuickCheck; // Will verify nothing if all pieces marked complete
            }

            var filePaths = GetTorrentFilePaths();
            bool filesModified = false;

            foreach (var path in filePaths)
            {
                if (!File.Exists(path))
                {
                    _logger.LogWarning("File {Path} missing, full verification required", path);
                    return VerificationMode.Full;
                }

                var fileInfo = new FileInfo(path);

                if (fileInfo.LastWriteTimeUtc > lastActive)
                {
                    _logger.LogWarning(
                        "File {Path} modified externally (last modified: {Modified}, last active: {Active}), full verification required",
                        path, fileInfo.LastWriteTimeUtc, lastActive);
                    filesModified = true;
                }
            }

            if (filesModified)
            {
                return VerificationMode.Full;
            }

            _logger.LogDebug("Files unchanged since last session, using quick verification");
            return VerificationMode.QuickCheck;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error determining verification mode, defaulting to full verification");
            return VerificationMode.Full;
        }
    }

    /// <summary>
    /// Verify file integrity of downloaded pieces.
    /// Called automatically during resume, can also be called manually.
    /// </summary>
    internal async Task<VerificationResult> VerifyIntegrityAsync(
        VerificationOptions options = null,
        IProgress<VerificationProgress> progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= VerificationOptions.Default;

        _logger.LogDebug("Starting integrity verification for {Name} (mode: {Mode})",
            _torrent.DisplayName, options.Mode);

        var result = new VerificationResult
        {
            TotalPieces = _torrent.PieceCount,
            StartTime = DateTime.UtcNow
        };

        try
        {
            var localBitfield = _getLocalBitfield();

            // Determine which pieces to verify
            var piecesToVerify = options.Mode switch
            {
                VerificationMode.Full => Enumerable.Range(0, _torrent.PieceCount),
                VerificationMode.QuickCheck => GetCompletedPieces(localBitfield),
                VerificationMode.Selective => options.PieceRange ?? Enumerable.Empty<int>(),
                _ => throw new ArgumentException($"Invalid verification mode: {options.Mode}")
            };

            var piecesToVerifyList = piecesToVerify.ToList();

            if (piecesToVerifyList.Count == 0)
            {
                _logger.LogDebug("No pieces to verify (mode: {Mode})", options.Mode);
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
                result.Success = true;
                return result;
            }

            _logger.LogDebug("Verifying {Count} pieces...", piecesToVerifyList.Count);

            // Parallel verification with progress reporting
            var verifiedCount = 0;
            var corruptPieces = new ConcurrentBag<int>();
            var missingPieces = new ConcurrentBag<int>();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism
                    ?? Math.Max(1, Environment.ProcessorCount / 2),
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(
                piecesToVerifyList,
                parallelOptions,
                async (pieceIndex, ct) =>
                {
                    var readResult = await _pieceManager.ReadPieceAsync(pieceIndex).ConfigureAwait(false);

                    if (!readResult.IsSuccess)
                    {
                        // Missing or unreadable piece
                        missingPieces.Add(pieceIndex);
                        _logger.LogWarning("Piece {Index} could not be read: {Error}",
                            pieceIndex, readResult.ErrorMessage);
                    }
                    else
                    {
                        var isValid = _pieceManager.VerifyPiece(pieceIndex, readResult.Data);

                        if (isValid)
                        {
                            // Verified successfully
                        }
                        else
                        {
                            corruptPieces.Add(pieceIndex);
                            _logger.LogWarning("Piece {Index} failed verification (corrupt)", pieceIndex);

                            // Optionally mark as incomplete for re-download
                            if (options.AutoRedownloadCorrupt)
                            {
                                localBitfield.ClearPiece(pieceIndex);
                            }
                        }
                    }

                    // Report progress
                    var completed = Interlocked.Increment(ref verifiedCount);
                    progress?.Report(new VerificationProgress
                    {
                        TotalPieces = piecesToVerifyList.Count,
                        VerifiedPieces = completed,
                        CorruptCount = corruptPieces.Count,
                        Percentage = (double)completed / piecesToVerifyList.Count
                    });
                }).ConfigureAwait(false);

            // Collect results
            result.VerifiedPieces = piecesToVerifyList.Except(corruptPieces).Except(missingPieces).ToList();
            result.CorruptPieces = corruptPieces.ToList();
            result.MissingPieces = missingPieces.ToList();
            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;
            result.Success = result.CorruptPieces.Count == 0 && result.MissingPieces.Count == 0;

            _logger.LogDebug(
                "Verification complete: {Summary} in {Duration:F2}s",
                result.Summary,
                result.Duration.TotalSeconds);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Verification cancelled by user");
            result.Cancelled = true;
            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during integrity verification");
            result.Error = ex;
            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;
            return result;
        }
    }

    /// <summary>
    /// Verification logic specifically for resume flow.
    /// </summary>
    internal async Task<VerificationResult> VerifyIntegrityOnResumeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Verifying integrity on resume...");

        // Determine verification mode based on file modification times
        var verificationMode = await DetermineVerificationModeAsync().ConfigureAwait(false);

        var options = new VerificationOptions
        {
            Mode = verificationMode,
            AutoRedownloadCorrupt = true // Always auto-redownload on resume
        };

        // Create progress reporter (logs to console/file)
        var progress = new Progress<VerificationProgress>(p =>
        {
            if (p.VerifiedPieces % 100 == 0 || p.Percentage >= 1.0)
            {
                _logger.LogDebug(
                    "Verification progress: {Progress:P2} ({Verified}/{Total}, {Corrupt} corrupt)",
                    p.Percentage, p.VerifiedPieces, p.TotalPieces, p.CorruptCount);
            }
        });

        var result = await VerifyIntegrityAsync(options, progress, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Resume verification complete: {Summary}",
            result.Summary);

        return result;
    }

    private static IEnumerable<int> GetCompletedPieces(Bitfield localBitfield)
    {
        for (int i = 0; i < localBitfield.PieceCount; i++)
        {
            if (localBitfield.HasPiece(i))
            {
                yield return i;
            }
        }
    }
}
