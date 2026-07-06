using System.Buffers;
using FluentAssertions;
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

public class SendBufferIntegrationTests : IDisposable
{
    private const int BlockSize = 16384;
    private const int PieceCount = 10;
    private const int BlocksPerPiece = 4;
    private readonly Mock<IDiskBackend> _diskBackendMock;
    private readonly PeerSendBufferManager _manager;
    private readonly CancellationTokenSource _cts = new();

    public SendBufferIntegrationTests()
    {
        var torrentInfo = MockFactories.CreateTorrentInfo(
            pieceCount: PieceCount, pieceLength: BlockSize * BlocksPerPiece);
        _diskBackendMock = new Mock<IDiskBackend>();
        _diskBackendMock
            .Setup(d => d.ReadAsync(It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, long _, Memory<byte> buf, CancellationToken _) =>
            {
                // Fill with recognizable pattern
                buf.Span.Fill(0xAB);
                return buf.Length;
            });

        var pieceMapper = new PieceMapper("/tmp/test", torrentInfo);
        var peerSettings = new PeerSettings
        {
            SendBufferWatermark = 10 * BlockSize, // manual ceiling: 10 blocks
            SendBufferLowWatermark = BlockSize,
            SendBufferWatermarkFactor = 50
        };
        var monitor = new Mock<IOptionsMonitor<PeerSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(peerSettings);

        _manager = new PeerSendBufferManager(
            _diskBackendMock.Object,
            pieceMapper,
            monitor.Object,
            torrentInfo,
            NullLogger<PeerSendBufferManager>.Instance,
            _cts.Token);
    }

    [Fact]
    public async Task FullFlow_Unchoke_ReadAhead_Serve_Choke()
    {
        var peer = MockFactories.CreatePeerConnectionMock().Object;

        // 1. Unchoke -> read-ahead starts
        _manager.OnPeerUnchoked(this, new PeerChokeChangedEventArgs(peer, isChoked: false));
        await Task.Delay(200); // Let read-ahead fill buffer

        // 2. Serve — should hit buffer
        var hit = _manager.TryServe(peer, 0, 0, BlockSize, out var entry);
        hit.Should().BeTrue();
        entry.Length.Should().Be(BlockSize);
        ArrayPool<byte>.Shared.Return(entry.Data);

        // 3. Choke -> cleanup. Buffer removal happens asynchronously in the
        // read-ahead loop's finally block, so poll instead of guessing a delay.
        _manager.OnPeerChoked(this, new PeerChokeChangedEventArgs(peer, isChoked: true));
        await WaitForConditionAsync(
            () => _manager.GetStats().ActivePeerBuffers == 0,
            "active peer buffers to drain after choke");

        _manager.GetStats().ActivePeerBuffers.Should().Be(0);
        _manager.GetStats().BufferHits.Should().Be(1);
    }

    [Fact]
    public async Task RapidUnchokeChoke_NoLeakedArrayPoolRentals()
    {
        var peer = MockFactories.CreatePeerConnectionMock().Object;

        // Rapidly unchoke then immediately choke
        _manager.OnPeerUnchoked(this, new PeerChokeChangedEventArgs(peer, isChoked: false));
        _manager.OnPeerChoked(this, new PeerChokeChangedEventArgs(peer, isChoked: true));

        // Cleanup completes asynchronously in the read-ahead finally block.
        await WaitForConditionAsync(
            () => _manager.GetStats().ActivePeerBuffers == 0
                  && _manager.GetStats().TotalBufferedBytes == 0,
            "buffers to be released after rapid unchoke/choke");

        // Verify no leaked buffers
        _manager.GetStats().TotalBufferedBytes.Should().Be(0);
        _manager.GetStats().ActivePeerBuffers.Should().Be(0);
    }

    [Fact]
    public async Task MultiPeer_GuidedReduction_UnderPressure()
    {
        // With a small ceiling (10 blocks), multiple peers should not exceed it
        var peers = Enumerable.Range(0, 5)
            .Select(_ => MockFactories.CreatePeerConnectionMock().Object)
            .ToList();

        foreach (var peer in peers)
            _manager.OnPeerUnchoked(this, new PeerChokeChangedEventArgs(peer, isChoked: false));

        await Task.Delay(500);

        // Total buffered should not exceed ceiling (10 blocks = 163840 bytes)
        _manager.GetStats().TotalBufferedBytes.Should().BeLessThanOrEqualTo(10 * BlockSize);
        _manager.GetStats().ActivePeerBuffers.Should().Be(5);

        foreach (var peer in peers)
            _manager.OnPeerChoked(this, new PeerChokeChangedEventArgs(peer, isChoked: true));
    }

    [Fact]
    public async Task PauseResume_CleansAndRebuildsBuffers()
    {
        var peer = MockFactories.CreatePeerConnectionMock().Object;
        _manager.OnPeerUnchoked(this, new PeerChokeChangedEventArgs(peer, isChoked: false));
        await Task.Delay(200);

        // Simulate engine pause. CancelAll() cancels the per-peer tokens but the
        // buffers are removed asynchronously in each read-ahead loop's finally
        // block, so poll for the cleaned state instead of a fixed delay.
        _manager.CancelAll();
        await WaitForConditionAsync(
            () => _manager.GetStats().ActivePeerBuffers == 0
                  && _manager.GetStats().TotalBufferedBytes == 0,
            "buffers to be cleaned after pause");

        _manager.GetStats().ActivePeerBuffers.Should().Be(0);
        _manager.GetStats().TotalBufferedBytes.Should().Be(0);
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it returns true or the timeout
    /// elapses. Replaces arbitrary Task.Delay guesses for async cleanup that can
    /// run slower under CI load (the original Windows CI flake).
    /// </summary>
    private static async Task WaitForConditionAsync(
        Func<bool> condition, string description, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException(
                    $"Timed out after {timeoutMs}ms waiting for {description}.");
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _manager.Dispose();
        _cts.Dispose();
    }
}
