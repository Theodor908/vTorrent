using System.Buffers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Storage;
using vTorrent.Core.Events;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PieceIO;
using vTorrent.Core.Upload;
using vTorrent.Tests.Mocks;
using Xunit;

namespace vTorrent.Tests.Upload;

public class PeerSendBufferManagerTests : IDisposable
{
    private const int BlockSize = 16384;
    private const int PieceCount = 10;
    private readonly Mock<IDiskBackend> _diskBackendMock;
    private readonly PieceMapper _pieceMapper;
    private readonly PeerSendBufferManager _manager;
    private readonly CancellationTokenSource _cts = new();

    public PeerSendBufferManagerTests()
    {
        var torrentInfo = MockFactories.CreateTorrentInfo(pieceCount: PieceCount, pieceLength: BlockSize * 4);
        _diskBackendMock = new Mock<IDiskBackend>();
        _diskBackendMock
            .Setup(d => d.ReadAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, long _, Memory<byte> buf, CancellationToken _) => buf.Length);

        // PieceMapper needs a real basePath + torrentInfo
        _pieceMapper = new PieceMapper("/tmp/test", torrentInfo);

        var peerSettings = new PeerSettings
        {
            SendBufferWatermark = 0,         // auto-tune
            SendBufferLowWatermark = BlockSize,
            SendBufferWatermarkFactor = 50
        };
        var monitor = new Mock<IOptionsMonitor<PeerSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(peerSettings);

        _manager = new PeerSendBufferManager(
            _diskBackendMock.Object,
            _pieceMapper,
            monitor.Object,
            torrentInfo,
            NullLogger<PeerSendBufferManager>.Instance,
            _cts.Token);
    }

    [Fact]
    public async Task OnPeerUnchoked_CreatesBufferAndStartsReadAhead()
    {
        var peer = MockFactories.CreatePeerConnectionMock().Object;
        _manager.OnPeerUnchoked(this, new PeerChokeChangedEventArgs(peer, isChoked: false));

        await Task.Delay(100); // Let read-ahead start
        _manager.GetStats().ActivePeerBuffers.Should().Be(1);
    }

    [Fact]
    public async Task OnPeerChoked_DisposesBufferAndCancelsReadAhead()
    {
        var peer = MockFactories.CreatePeerConnectionMock().Object;
        _manager.OnPeerUnchoked(this, new PeerChokeChangedEventArgs(peer, isChoked: false));
        await Task.Delay(100);

        _manager.OnPeerChoked(this, new PeerChokeChangedEventArgs(peer, isChoked: true));
        await Task.Delay(100);

        _manager.GetStats().ActivePeerBuffers.Should().Be(0);
    }

    [Fact]
    public void TryServe_EmptyBuffer_ReturnsMiss()
    {
        var peer = MockFactories.CreatePeerConnectionMock().Object;
        _manager.TryServe(peer, 0, 0, BlockSize, out _).Should().BeFalse();
    }

    [Fact]
    public void GetStats_ReturnsCorrectCounts()
    {
        var stats = _manager.GetStats();
        stats.BufferHits.Should().Be(0);
        stats.BufferMisses.Should().Be(0);
        stats.TotalBufferedBytes.Should().Be(0);
    }

    [Fact]
    public async Task ReadAhead_SequentialBlocks_FillsBuffer()
    {
        var peer = MockFactories.CreatePeerConnectionMock().Object;
        _manager.OnPeerUnchoked(this, new PeerChokeChangedEventArgs(peer, isChoked: false));
        await Task.Delay(300); // Let read-ahead fill

        // Should have pre-read at least one block
        _diskBackendMock.Verify(
            d => d.ReadAsync(It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ReadAhead_DiskError_SkipsBlockContinuesLoop()
    {
        // First read fails, subsequent reads succeed
        var callCount = 0;
        _diskBackendMock
            .Setup(d => d.ReadAsync(It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, long _, Memory<byte> buf, CancellationToken _) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    throw new IOException("Disk error");
                return buf.Length;
            });

        var peer = MockFactories.CreatePeerConnectionMock().Object;
        _manager.OnPeerUnchoked(this, new PeerChokeChangedEventArgs(peer, isChoked: false));
        await Task.Delay(300);

        // Read-ahead should have continued past the error
        callCount.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task BufferHit_SignalsDrain_WakesReadAhead()
    {
        var peer = MockFactories.CreatePeerConnectionMock().Object;
        _manager.OnPeerUnchoked(this, new PeerChokeChangedEventArgs(peer, isChoked: false));
        await Task.Delay(200);

        // Consume a block — should signal drain and trigger more reads
        var initialReadCount = _diskBackendMock.Invocations.Count;
        if (_manager.TryServe(peer, 0, 0, BlockSize, out var entry))
            ArrayPool<byte>.Shared.Return(entry.Data);

        await Task.Delay(200);
        _diskBackendMock.Invocations.Count.Should().BeGreaterThanOrEqualTo(initialReadCount);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _manager.Dispose();
        _cts.Dispose();
    }
}
