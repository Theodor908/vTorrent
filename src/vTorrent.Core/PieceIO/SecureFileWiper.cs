using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Interfaces.Storage;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.PieceIO;

/// <summary>
/// Securely wipes files by overwriting their bytes with random data before deletion.
/// Single random-data pass per NIST SP 800-88.
/// </summary>
public sealed class SecureFileWiper : ISecureFileWiper
{
    private const int BufferSize = 64 * 1024; // 64KB chunks

    private readonly ILogger _logger;

    public SecureFileWiper(ILoggerFactory loggerFactory)
    {
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger<SecureFileWiper>();
    }

    /// <inheritdoc/>
    public Task WipeFileAsync(string filePath,
        IProgress<SecureWipeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return WipeFilesAsync([filePath], progress, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task WipeFilesAsync(IReadOnlyList<string> filePaths,
        IProgress<SecureWipeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Pre-calculate total bytes across all files that exist
        long totalBytes = 0;
        foreach (var path in filePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    totalBytes += new FileInfo(path).Length;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not get file size for {FilePath}", path);
                }
            }
        }

        long totalBytesWiped = 0;
        int totalFiles = filePaths.Count;

        for (int fileIndex = 0; fileIndex < filePaths.Count; fileIndex++)
        {
            string filePath = filePaths[fileIndex];

            if (!File.Exists(filePath))
            {
                _logger.LogDebug("File does not exist, skipping: {FilePath}", filePath);
                continue;
            }

            long fileSize;
            try
            {
                fileSize = new FileInfo(filePath).Length;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not get file info for {FilePath}, skipping", filePath);
                continue;
            }

            try
            {
                long bytesWiped = await WipeSingleFileAsync(
                    filePath,
                    fileIndex,
                    totalFiles,
                    fileSize,
                    totalBytesWiped,
                    totalBytes,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                totalBytesWiped += bytesWiped;
                File.Delete(filePath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not wipe file {FilePath}, skipping", filePath);
            }
        }
    }

    /// <summary>
    /// Returns the number of bytes actually written to disk for the given file.
    /// </summary>
    private static async Task<long> WipeSingleFileAsync(
        string filePath,
        int fileIndex,
        int totalFiles,
        long fileSize,
        long totalBytesWipedSoFar,
        long totalBytes,
        IProgress<SecureWipeProgress>? progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BufferSize];

        if (fileSize == 0)
        {
            // Nothing to overwrite; report progress for zero-length file
            progress?.Report(new SecureWipeProgress(
                CurrentFile: filePath,
                FileIndex: fileIndex,
                TotalFiles: totalFiles,
                BytesWiped: 0,
                CurrentFileSize: 0,
                TotalBytesWiped: totalBytesWipedSoFar,
                TotalBytes: totalBytes));
            return 0;
        }

        using var fs = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous);

        long bytesWipedInFile = 0;
        long remaining = fileSize;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int chunkSize = (int)Math.Min(buffer.Length, remaining);
            RandomNumberGenerator.Fill(buffer.AsSpan(0, chunkSize));
            await fs.WriteAsync(buffer.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false);

            bytesWipedInFile += chunkSize;
            remaining -= chunkSize;

            progress?.Report(new SecureWipeProgress(
                CurrentFile: filePath,
                FileIndex: fileIndex,
                TotalFiles: totalFiles,
                BytesWiped: bytesWipedInFile,
                CurrentFileSize: fileSize,
                TotalBytesWiped: totalBytesWipedSoFar + bytesWipedInFile,
                TotalBytes: totalBytes));
        }

        await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
        return bytesWipedInFile;
    }
}
