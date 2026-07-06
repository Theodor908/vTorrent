using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Abstractions.Interfaces.Storage;

/// <summary>
/// Securely wipes files by overwriting their bytes with random data before deletion.
/// Single random-data pass per NIST SP 800-88.
/// </summary>
public interface ISecureFileWiper
{
    /// <summary>
    /// Overwrite a single file with random data, flush to disk, then delete.
    /// Silently skips files that do not exist.
    /// </summary>
    Task WipeFileAsync(string filePath,
        IProgress<SecureWipeProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Overwrite multiple files with random data, flush to disk, then delete each.
    /// Silently skips files that do not exist. Locked/read-only files are logged and skipped.
    /// </summary>
    Task WipeFilesAsync(IReadOnlyList<string> filePaths,
        IProgress<SecureWipeProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Progress report for secure file wiping operations.
/// </summary>
public record SecureWipeProgress(
    string CurrentFile,
    int FileIndex,
    int TotalFiles,
    long BytesWiped,
    long CurrentFileSize,
    long TotalBytesWiped,
    long TotalBytes);
