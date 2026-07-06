using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using vTorrent.Abstractions.Interfaces.Storage;
using vTorrent.Core;
using Xunit;
using vTorrent.Core.PieceIO;

namespace vTorrent.Tests.Unit.Core;

/// <summary>
/// A synchronous IProgress&lt;T&gt; implementation that collects reports without race conditions.
/// Unlike Progress&lt;T&gt; which posts to the synchronization context, this calls Report synchronously.
/// </summary>
file sealed class SyncProgress<T> : IProgress<T>
{
    private readonly List<T> _reports = [];
    private readonly Action<T>? _callback;

    public SyncProgress(Action<T>? callback = null)
    {
        _callback = callback;
    }

    public IReadOnlyList<T> Reports => _reports;

    public void Report(T value)
    {
        _reports.Add(value);
        _callback?.Invoke(value);
    }
}

/// <summary>
/// An IProgress&lt;T&gt; that cancels the provided CancellationTokenSource on the first report.
/// </summary>
file sealed class CancelOnFirstReport<T> : IProgress<T>
{
    private readonly CancellationTokenSource _cts;
    private bool _cancelled;

    public CancelOnFirstReport(CancellationTokenSource cts)
    {
        _cts = cts;
    }

    public void Report(T value)
    {
        if (!_cancelled)
        {
            _cancelled = true;
            _cts.Cancel();
        }
    }
}

public sealed class SecureFileWiperTests : IDisposable
{
    private readonly string _tempDir;

    private readonly SecureFileWiper _wiper;

    public SecureFileWiperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SecureWipeTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _wiper = new SecureFileWiper(NullLoggerFactory.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private string TempFile(string name) => Path.Combine(_tempDir, name);

    private static byte[] CreateBytes(int length, byte fill = 0x00)
    {
        var bytes = new byte[length];
        if (fill != 0x00)
            Array.Fill(bytes, fill);
        return bytes;
    }

    // -------------------------------------------------------------------------
    // Test 1
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WipeFileAsync_OverwritesBytes_ThenDeletes()
    {
        // Arrange
        string path = TempFile("wipe_deletes.bin");
        await File.WriteAllBytesAsync(path, CreateBytes(4096));

        // Act
        await _wiper.WipeFileAsync(path);

        // Assert
        File.Exists(path).Should().BeFalse("the file should be deleted after wiping");
    }

    // -------------------------------------------------------------------------
    // Test 2
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WipeFileAsync_OverwritesWithDifferentData()
    {
        // Arrange
        string path = TempFile("wipe_overwrite.bin");
        const int size = 8192;
        byte[] original = CreateBytes(size, fill: 0xAA);
        await File.WriteAllBytesAsync(path, original);

        // Hold an open read handle so we can read the overwritten data before delete
        using var readHandle = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        // Act — wiper must open for write with FileShare.None, but the file is already
        // open for read with ReadWrite sharing, so the wiper can open for write too.
        await _wiper.WipeFileAsync(path);

        // Read the (now-deleted but still open) file through our handle
        readHandle.Seek(0, SeekOrigin.Begin);
        byte[] wiped = new byte[size];
        int bytesRead = await readHandle.ReadAsync(wiped.AsMemory());

        // Assert: bytes should no longer all be 0xAA
        bytesRead.Should().Be(size);
        wiped.All(b => b == 0xAA).Should().BeFalse(
            "the wiper must have overwritten the original data with random bytes");
    }

    // -------------------------------------------------------------------------
    // Test 3
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WipeFileAsync_ReportsProgress()
    {
        // Arrange
        string path = TempFile("progress.bin");
        const int size = 256 * 1024; // 256 KB
        await File.WriteAllBytesAsync(path, CreateBytes(size));
        var progress = new SyncProgress<SecureWipeProgress>();

        // Act
        await _wiper.WipeFileAsync(path, progress);

        // Assert
        progress.Reports.Should().NotBeEmpty("progress must be reported for a non-empty file");

        var last = progress.Reports[^1];
        last.BytesWiped.Should().Be(size, "all bytes of the file should have been wiped");
        last.TotalBytesWiped.Should().Be(size);
        last.TotalBytes.Should().Be(size);
        last.CurrentFileSize.Should().Be(size);
        last.TotalFiles.Should().Be(1);
        last.FileIndex.Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // Test 4
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WipeFilesAsync_ReportsProgressAcrossMultipleFiles()
    {
        // Arrange
        string p1 = TempFile("multi_a.bin");
        string p2 = TempFile("multi_b.bin");
        string p3 = TempFile("multi_c.bin");

        int[] sizes = [4096, 8192, 4096];
        await File.WriteAllBytesAsync(p1, CreateBytes(sizes[0]));
        await File.WriteAllBytesAsync(p2, CreateBytes(sizes[1]));
        await File.WriteAllBytesAsync(p3, CreateBytes(sizes[2]));

        long expectedTotal = sizes.Sum(s => (long)s);
        var progress = new SyncProgress<SecureWipeProgress>();

        // Act
        await _wiper.WipeFilesAsync([p1, p2, p3], progress);

        // Assert
        File.Exists(p1).Should().BeFalse();
        File.Exists(p2).Should().BeFalse();
        File.Exists(p3).Should().BeFalse();

        progress.Reports.Should().NotBeEmpty();
        var last = progress.Reports[^1];
        last.TotalBytesWiped.Should().Be(expectedTotal);
        last.TotalBytes.Should().Be(expectedTotal);
        last.TotalFiles.Should().Be(3);
    }

    // -------------------------------------------------------------------------
    // Test 5
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WipeFileAsync_LockedFile_SkipsGracefully()
    {
        // Arrange — hold exclusive lock
        string path = TempFile("locked.bin");
        await File.WriteAllBytesAsync(path, CreateBytes(4096));

        using var lockHandle = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        // Act & Assert — must not throw
        Func<Task> act = () => _wiper.WipeFileAsync(path);
        await act.Should().NotThrowAsync("locked files should be skipped, not cause an exception");

        // File still exists because wiper couldn't open it
        File.Exists(path).Should().BeTrue("a locked file should be left untouched");
    }

    // -------------------------------------------------------------------------
    // Test 6
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WipeFileAsync_MissingFile_SilentlySkips()
    {
        // Arrange
        string path = TempFile("does_not_exist.bin");

        // Act & Assert
        Func<Task> act = () => _wiper.WipeFileAsync(path);
        await act.Should().NotThrowAsync("missing files should be silently skipped");
    }

    // -------------------------------------------------------------------------
    // Test 7
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WipeFileAsync_ZeroLengthFile_HandledWithoutError()
    {
        // Arrange
        string path = TempFile("empty.bin");
        await File.WriteAllBytesAsync(path, []);

        // Act
        Func<Task> act = () => _wiper.WipeFileAsync(path);
        await act.Should().NotThrowAsync("zero-length files should be handled gracefully");

        // Assert — file is deleted
        File.Exists(path).Should().BeFalse("the zero-length file should be deleted");
    }

    // -------------------------------------------------------------------------
    // Test 8
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WipeFileAsync_CancellationStopsEarly()
    {
        // Arrange — 1MB file so there are multiple 64KB chunks to trigger cancellation
        string path = TempFile("cancel.bin");
        const int size = 1 * 1024 * 1024;
        await File.WriteAllBytesAsync(path, CreateBytes(size));

        using var cts = new CancellationTokenSource();
        var progress = new CancelOnFirstReport<SecureWipeProgress>(cts);

        // Act & Assert
        Func<Task> act = () => _wiper.WipeFileAsync(path, progress, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation should propagate as OperationCanceledException");
    }

    // -------------------------------------------------------------------------
    // Test 9
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WipeFilesAsync_SkipsMissingFilesInBatch()
    {
        // Arrange
        string existingPath = TempFile("exists.bin");
        string missingPath = TempFile("missing.bin");
        await File.WriteAllBytesAsync(existingPath, CreateBytes(4096));

        // Act & Assert — must not throw
        Func<Task> act = () => _wiper.WipeFilesAsync([existingPath, missingPath]);
        await act.Should().NotThrowAsync("missing files in a batch must be silently skipped");

        // Existing file was wiped
        File.Exists(existingPath).Should().BeFalse("the existing file should have been wiped and deleted");
        // Missing file obviously still doesn't exist
        File.Exists(missingPath).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Test 10 — Performance: wipe must complete within reasonable time
    // -------------------------------------------------------------------------
    [Fact]
    public async Task WipeFileAsync_LargeFile_CompletesWithinTimeout()
    {
        // Arrange — 10MB file; with WriteThrough this would be ~10K fsyncs
        string path = TempFile("perf_test.bin");
        const int size = 10 * 1024 * 1024;
        await File.WriteAllBytesAsync(path, CreateBytes(size));

        // Act — must complete within 10 seconds (WriteThrough on HDD could take 30+)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Func<Task> act = () => _wiper.WipeFileAsync(path, cancellationToken: cts.Token);

        // Assert
        await act.Should().NotThrowAsync<OperationCanceledException>(
            "10MB wipe should complete well within 10 seconds with buffered I/O");
        File.Exists(path).Should().BeFalse();
    }
}
