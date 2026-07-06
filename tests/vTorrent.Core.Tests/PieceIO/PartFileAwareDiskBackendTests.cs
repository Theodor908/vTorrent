using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Storage;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.PieceIO;
using vTorrent.Core.PieceIO.Backends;
using vTorrent.Tests.Mocks;
using Xunit;

namespace vTorrent.Tests.Unit.PieceIO;

/// <summary>
/// Unit tests for <see cref="PartFileAwareDiskBackend"/>.
///
/// Setup: 3 files, 10 pieces × 16 384 bytes.  File at index 1 is initially Skip.
/// The inner backend is a <see cref="Mock{IDiskBackend}"/>.
/// </summary>
public class PartFileAwareDiskBackendTests : IAsyncDisposable
{
    // ------------------------------------------------------------------ //
    //  Constants / setup
    // ------------------------------------------------------------------ //

    private const int PieceCount  = 10;
    private const int PieceLength = 16384;
    private const int FileCount   = 3;

    private readonly string _tempDir;
    private readonly TorrentInfo _torrentInfo;
    private readonly PieceMapper _pieceMapper;
    private readonly Mock<IDiskBackend> _innerMock;
    private readonly ILogger _logger;

    // File paths as reported by PieceMapper
    private readonly string _file0Path;
    private readonly string _file1Path;
    private readonly string _file2Path;

    public PartFileAwareDiskBackendTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PFADBTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _torrentInfo = MockFactories.CreateMultiFileTorrentInfo(
            pieceCount: PieceCount,
            pieceLength: PieceLength,
            fileCount: FileCount);

        _pieceMapper  = new PieceMapper(_tempDir, _torrentInfo);
        _innerMock    = new Mock<IDiskBackend>();
        _logger       = new Mock<ILogger>().Object;

        // Standard setups so the mock doesn't throw.
        _innerMock
            .Setup(b => b.WriteAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        _innerMock
            .Setup(b => b.ReadAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _innerMock
            .Setup(b => b.EnsureAllocatedAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        _innerMock
            .Setup(b => b.DisposeAsync())
            .Returns(ValueTask.CompletedTask);
        _innerMock
            .Setup(b => b.FlushAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        _innerMock
            .Setup(b => b.CloseFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        _innerMock
            .Setup(b => b.CloseAllAsync(It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        _innerMock
            .Setup(b => b.GetStats())
            .Returns(new DiskBackendStats(0, 0, 0, 0, 0));

        // Resolve the paths from the mapper.
        var mappings = _pieceMapper.FileMappings;
        _file0Path = mappings[0].FilePath;
        _file1Path = mappings[1].FilePath;
        _file2Path = mappings[2].FilePath;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // ignore cleanup errors
        }

        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ //
    //  Factory helpers
    // ------------------------------------------------------------------ //

    private PartFileAwareDiskBackend CreateBackend(FilePriority[]? priorities = null)
    {
        priorities ??= new[]
        {
            FilePriority.Normal,    // file 0
            FilePriority.Skip,      // file 1 (skipped by default)
            FilePriority.Normal,    // file 2
        };

        return new PartFileAwareDiskBackend(
            _innerMock.Object,
            _pieceMapper,
            _torrentInfo,
            _tempDir,
            "aabbccdd",
            priorities,
            _logger);
    }

    private static byte[] MakeData(int length, byte fill = 0xAB)
    {
        var data = new byte[length];
        Array.Fill(data, fill);
        return data;
    }

    // ------------------------------------------------------------------ //
    //  Test 1: Write to skipped file routes to partfile, NOT inner
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task WriteAsync_SkippedFile_RoutesToPartFile()
    {
        await using var backend = CreateBackend();

        var data = MakeData(512, 0x11);
        await backend.WriteAsync(_file1Path, fileOffset: 0, data.AsMemory());

        // Inner backend must NOT have been called.
        _innerMock.Verify(
            b => b.WriteAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Compute which piece the write landed on.
        // File 1 starts at torrent-offset = file0.Length.
        // With 10 pieces x 16384 bytes and 3 equal files:
        //   file0.Length = 10*16384/3 ≈ 54613, pieceIndex = 54613/16384 = 3
        var reverseMapper = new FileOffsetToPieceMapper(_pieceMapper);
        var (expectedPiece, _) = reverseMapper.Map(fileIndex: 1, fileOffset: 0);

        backend.HasPieceInPartFile(expectedPiece).Should().BeTrue(
            "a write to a skipped file should land in the partfile");
    }

    // ------------------------------------------------------------------ //
    //  Test 2: Write to wanted file delegates to inner
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task WriteAsync_WantedFile_DelegatesToInner()
    {
        await using var backend = CreateBackend();

        var data = MakeData(512, 0x22);
        await backend.WriteAsync(_file0Path, fileOffset: 0, data.AsMemory());

        _innerMock.Verify(
            b => b.WriteAsync(_file0Path, 0L, It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ------------------------------------------------------------------ //
    //  Test 3: Read from skipped file routes to partfile, NOT inner
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ReadAsync_SkippedFile_RoutesToPartFile()
    {
        await using var backend = CreateBackend();

        // First write some data to the partfile.
        var written = MakeData(PieceLength, 0x33);
        await backend.WriteAsync(_file1Path, fileOffset: 0, written.AsMemory());

        // Now read it back via the same path.
        var readBuf = new byte[PieceLength];
        var bytesRead = await backend.ReadAsync(_file1Path, fileOffset: 0, readBuf.AsMemory());

        bytesRead.Should().BeGreaterThan(0, "partfile should return stored data");
        readBuf.AsSpan(0, bytesRead).ToArray().Should().BeEquivalentTo(
            written.AsSpan(0, bytesRead).ToArray(),
            "data read from partfile should match what was written");

        // Inner must NOT have been involved in reads for the skipped file.
        _innerMock.Verify(
            b => b.ReadAsync(_file1Path, It.IsAny<long>(), It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------ //
    //  Test 4: EnsureAllocatedAsync for skipped file is a no-op
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task EnsureAllocatedAsync_SkippedFile_NoOps()
    {
        await using var backend = CreateBackend();

        await backend.EnsureAllocatedAsync(_file1Path, requiredSize: 1024);

        _innerMock.Verify(
            b => b.EnsureAllocatedAsync(_file1Path, It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------ //
    //  Test 5: EnsureAllocatedAsync for wanted file delegates to inner
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task EnsureAllocatedAsync_WantedFile_Delegates()
    {
        await using var backend = CreateBackend();

        await backend.EnsureAllocatedAsync(_file0Path, requiredSize: 1024);

        _innerMock.Verify(
            b => b.EnsureAllocatedAsync(_file0Path, 1024L, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ------------------------------------------------------------------ //
    //  Test 6: Skip → Normal exports data, subsequent writes go to inner
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task OnSingleFilePriorityChanged_SkipToNormal_ExportsData()
    {
        await using var backend = CreateBackend();

        // Write data to the skipped file (ends up in partfile).
        var written = MakeData(PieceLength, 0x55);
        await backend.WriteAsync(_file1Path, fileOffset: 0, written.AsMemory());

        // Raise priority to Normal — should export and then route to inner.
        await backend.OnSingleFilePriorityChangedAsync(1, FilePriority.Normal);

        // Subsequent writes to file 1 must now go to inner.
        var newData = MakeData(512, 0x66);
        await backend.WriteAsync(_file1Path, fileOffset: 0, newData.AsMemory());

        _innerMock.Verify(
            b => b.WriteAsync(_file1Path, It.IsAny<long>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ------------------------------------------------------------------ //
    //  Test 7: Normal → Skip causes subsequent writes to route to partfile
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task OnFilePrioritiesChanged_NormalToSkip_SetsUsePartfile()
    {
        // Start with all files Normal.
        var allNormal = new[]
        {
            FilePriority.Normal,
            FilePriority.Normal,
            FilePriority.Normal,
        };

        await using var backend = CreateBackend(allNormal);

        // Change file 0 to Skip — no on-disk file exists, so _usePartfile[0] should be true.
        var newPriorities = new[]
        {
            FilePriority.Skip,
            FilePriority.Normal,
            FilePriority.Normal,
        };
        await backend.OnFilePrioritiesChangedAsync(newPriorities);

        // Now a write to file 0 should NOT reach the inner backend.
        var data = MakeData(512, 0x77);
        await backend.WriteAsync(_file0Path, fileOffset: 0, data.AsMemory());

        _innerMock.Verify(
            b => b.WriteAsync(_file0Path, It.IsAny<long>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // The piece should be stored in the partfile.
        backend.HasPieceInPartFile(0).Should().BeTrue(
            "write to newly-skipped file should land in partfile");
    }
}
