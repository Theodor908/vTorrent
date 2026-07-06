using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Abstractions.Settings;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.PieceIO;
using vTorrent.Core.PieceIO.Backends;
using vTorrent.Tests.Mocks;
using Xunit;

namespace vTorrent.Tests.Unit.PieceIO;

/// <summary>
/// Integration-level regression test mirroring the real seed-transition bug:
/// <see cref="PieceManager"/> calls <c>ReleaseWriteHandlesAsync</c> -&gt; <c>CloseAllAsync</c>
/// when a torrent completes and starts seeding, expecting subsequent reads (uploads) to lazily
/// reopen handles. Before the fix, <see cref="FileHandleCache{THandle}"/> stayed permanently
/// "draining" after <c>CloseAllAsync</c>, so every read after the seed transition threw and the
/// client uploaded zero bytes forever.
/// </summary>
public class PosixDiskBackendDrainRecoveryTests : IAsyncDisposable
{
    private const int PieceCount = 4;
    private const int PieceLength = 16384;

    private readonly string _tempDir;
    private readonly TorrentInfo _torrentInfo;
    private readonly SparseFileManager _sparseFileManager;
    private readonly Mock<IFileLockManager> _lockManagerMock;
    private readonly string _filePath;

    public PosixDiskBackendDrainRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FHCDrainTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _torrentInfo = MockFactories.CreateTorrentInfo(PieceCount, PieceLength);
        _sparseFileManager = new SparseFileManager(_tempDir, _torrentInfo);

        _lockManagerMock = new Mock<IFileLockManager>();
        _lockManagerMock
            .Setup(m => m.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NoOpDisposable());

        _filePath = Path.GetFullPath(Path.Combine(_tempDir, _torrentInfo.Name));
    }

    public async ValueTask DisposeAsync()
    {
        _sparseFileManager.Dispose();

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

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private PosixDiskBackend CreateBackend() => new(
        _sparseFileManager,
        _lockManagerMock.Object,
        new DiskSettings(),
        writeModeOverride: null,
        logger: new Mock<ILogger>().Object);

    [Fact]
    public async Task WriteThenCloseAllThenRead_ReturnsCorrectBytes()
    {
        await using var backend = CreateBackend();

        var written = new byte[512];
        Array.Fill(written, (byte)0xCD);
        await backend.WriteAsync(_filePath, fileOffset: 0, written.AsMemory());

        // Simulates the seed transition: PieceManager.ReleaseWriteHandlesAsync() -> CloseAllAsync().
        await backend.CloseAllAsync();

        // Before the fix: ReadAsync throws InvalidOperationException("FileHandleCache is draining.")
        // After the fix: the handle is lazily reopened and the previously written bytes are returned.
        var readBuf = new byte[512];
        var bytesRead = await backend.ReadAsync(_filePath, fileOffset: 0, readBuf.AsMemory());

        bytesRead.Should().Be(512);
        readBuf.Should().BeEquivalentTo(written,
            "data written before the seed-transition CloseAllAsync must still be readable afterward");
    }
}
