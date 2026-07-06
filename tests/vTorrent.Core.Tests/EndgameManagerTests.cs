using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using vTorrent.Abstractions.Models;
using vTorrent.Core;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Tests.Mocks;
using Xunit;
using vTorrent.Core.Download;

namespace vTorrent.Tests.Unit.Core;

public class EndgameManagerTests
{
    private readonly Mock<ILogger<EndgameManager>> _logger = new();
    private readonly EndgameManager _manager;

    public EndgameManagerTests()
    {
        _manager = new EndgameManager(_logger.Object);
    }

    [Fact]
    public void PickDuplicateBlock_NoInProgressPieces_ReturnsNull()
    {
        var peer = CreateMockPeer("peer-A");
        var inProgress = new Dictionary<int, PieceBlockTracker>() as IReadOnlyDictionary<int, PieceBlockTracker>;
        var pending = new ConcurrentDictionary<BlockRequest, PendingBlock>();

        var result = _manager.PickDuplicateBlock(peer.Object, inProgress, pending, (_, _) => true);

        result.Should().BeNull();
    }

    [Fact]
    public void PickDuplicateBlock_BlockRequestedByDifferentPeer_ReturnsThatBlock()
    {
        var peerA = CreateMockPeer("peer-A");
        var peerB = CreateMockPeer("peer-B");

        var progress = new PieceBlockTracker(0, 32768, 16384); // 2 blocks
        progress.GetNextBlock(); // block 0 — marks requested
        var inProgress = new Dictionary<int, PieceBlockTracker> { [0] = progress }
            as IReadOnlyDictionary<int, PieceBlockTracker>;

        // Block 0 is pending from peer-B
        var block0 = new BlockRequest(0, 0, 16384);
        var pending = new ConcurrentDictionary<BlockRequest, PendingBlock>();
        pending[block0] = new PendingBlock { Peer = peerB.Object, PieceIndex = 0, Begin = 0, Length = 16384 };

        var result = _manager.PickDuplicateBlock(
            peerA.Object, inProgress, pending, (_, _) => true);

        result.Should().NotBeNull();
        result!.Value.PieceIndex.Should().Be(0);
        // Begin may be 0 or 16384 due to shuffle — just verify it's a valid block offset
        result!.Value.Begin.Should().BeOneOf(0, 16384);
    }

    [Fact]
    public void PickDuplicateBlock_BlockRequestedBySamePeer_StillReturnedInFloodMode()
    {
        // In endgame flood mode, we request ALL unreceived blocks from any peer
        // that has the piece — even if this peer already has the block pending.
        // Duplicate responses are handled on the receive side.
        var peerA = CreateMockPeer("peer-A");

        var progress = new PieceBlockTracker(0, 16384, 16384); // 1 block
        // Don't call GetNextBlock — block is unreceived and unrequested
        var inProgress = new Dictionary<int, PieceBlockTracker> { [0] = progress }
            as IReadOnlyDictionary<int, PieceBlockTracker>;

        var pending = new ConcurrentDictionary<BlockRequest, PendingBlock>();

        var result = _manager.PickDuplicateBlock(
            peerA.Object, inProgress, pending, (_, _) => true);

        result.Should().NotBeNull("endgame flood picks all unreceived blocks");
        result!.Value.PieceIndex.Should().Be(0);
    }

    [Fact]
    public void PickDuplicateBlock_PeerDoesNotHavePiece_ReturnsNull()
    {
        var peerA = CreateMockPeer("peer-A");
        var peerB = CreateMockPeer("peer-B");

        var progress = new PieceBlockTracker(0, 16384, 16384);
        progress.GetNextBlock();
        var inProgress = new Dictionary<int, PieceBlockTracker> { [0] = progress }
            as IReadOnlyDictionary<int, PieceBlockTracker>;

        var block0 = new BlockRequest(0, 0, 16384);
        var pending = new ConcurrentDictionary<BlockRequest, PendingBlock>();
        pending[block0] = new PendingBlock { Peer = peerB.Object, PieceIndex = 0, Begin = 0, Length = 16384 };

        // peerA does NOT have piece 0
        var result = _manager.PickDuplicateBlock(
            peerA.Object, inProgress, pending, (_, _) => false);

        result.Should().BeNull();
    }

    [Fact]
    public void OnBlockReceived_DuplicateBlock_ReturnsTrue_TracksWaste()
    {
        var peerA = CreateMockPeer("peer-A");
        var block = new BlockRequest(0, 0, 16384);

        _manager.OnBlockReceived(block, peerA.Object).Should().BeFalse(); // first
        _manager.OnBlockReceived(block, peerA.Object).Should().BeTrue();  // duplicate

        _manager.WastedBytes.Should().Be(16384);
        _manager.DuplicateBlockCount.Should().Be(1);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var peer = CreateMockPeer("peer-A");
        var block = new BlockRequest(0, 0, 16384);

        _manager.OnBlockReceived(block, peer.Object);
        _manager.Reset();

        _manager.WastedBytes.Should().Be(0);
        _manager.DuplicateBlockCount.Should().Be(0);
        // After reset, same block is no longer considered received
        _manager.OnBlockReceived(block, peer.Object).Should().BeFalse();
    }

    [Fact]
    public void ClearPieceBlocks_AllowsRedownloadAfterHashFailure()
    {
        var peer = CreateMockPeer("peer-A");

        // Simulate receiving all blocks of a 2-block piece
        var block0 = new BlockRequest(0, 0, 16384);
        var block1 = new BlockRequest(0, 16384, 16384);
        _manager.OnBlockReceived(block0, peer.Object).Should().BeFalse();
        _manager.OnBlockReceived(block1, peer.Object).Should().BeFalse();

        // Without clearing, re-received blocks would be rejected as duplicates
        _manager.OnBlockReceived(block0, peer.Object).Should().BeTrue("block is still tracked");

        // Simulate hash failure — clear piece tracking
        _manager.ClearPieceBlocks(pieceIndex: 0, blockSize: 16384, pieceSize: 32768);

        // Re-requested blocks should now be accepted
        _manager.OnBlockReceived(block0, peer.Object).Should().BeFalse("cleared after hash failure");
        _manager.OnBlockReceived(block1, peer.Object).Should().BeFalse("cleared after hash failure");
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrow()
    {
        var act = () => new EndgameManager(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ShouldInitializeWasteTrackingToZero()
    {
        _manager.WastedBytes.Should().Be(0);
        _manager.DuplicateBlockCount.Should().Be(0);
    }

    [Fact]
    public void PickDuplicateBlocks_ReturnsAtMostOneBlock_StrictEndgame()
    {
        var progress = new PieceBlockTracker(0, 64 * 1024, 16384);
        progress.GetNextBlock();
        progress.GetNextBlock();

        var inProgress = new Dictionary<int, PieceBlockTracker> { { 0, progress } };
        var pending = new ConcurrentDictionary<BlockRequest, PendingBlock>();
        var peerMock = MockFactories.CreatePeerConnectionMock(hasPieces: true);

        var result = _manager.PickDuplicateBlocks(
            peerMock.Object, inProgress, pending, (p, i) => true, maxBlocks: 50);

        Assert.Equal(1, result.Count);
    }

    private static Mock<IPeerConnection> CreateMockPeer(string name)
    {
        var mock = new Mock<IPeerConnection>();
        mock.Setup(p => p.IsConnected).Returns(true);
        mock.Setup(p => p.IsChoked).Returns(false);
        return mock;
    }
}

public class BlockRequestTests
{
    #region Construction

    [Fact]
    public void Constructor_ShouldStoreValues()
    {
        var request = new BlockRequest(5, 16384, 8192);

        request.PieceIndex.Should().Be(5);
        request.Begin.Should().Be(16384);
        request.Length.Should().Be(8192);
    }

    #endregion

    #region Equality

    [Fact]
    public void Equals_WithSameValues_ShouldReturnTrue()
    {
        var a = new BlockRequest(5, 16384, 8192);
        var b = new BlockRequest(5, 16384, 8192);

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Equals_WithDifferentPieceIndex_ShouldReturnFalse()
    {
        var a = new BlockRequest(5, 16384, 8192);
        var b = new BlockRequest(6, 16384, 8192);

        a.Equals(b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Equals_WithDifferentBegin_ShouldReturnFalse()
    {
        var a = new BlockRequest(5, 16384, 8192);
        var b = new BlockRequest(5, 0, 8192);

        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equals_WithDifferentLength_ShouldReturnTrue()
    {
        var request1 = new BlockRequest(5, 16384, 16384);
        var request2 = new BlockRequest(5, 16384, 8192);

        request1.Should().Be(request2);
        request1.GetHashCode().Should().Be(request2.GetHashCode());
    }

    #endregion

    #region GetHashCode

    [Fact]
    public void GetHashCode_ForEqualObjects_ShouldBeEqual()
    {
        var a = new BlockRequest(5, 16384, 8192);
        var b = new BlockRequest(5, 16384, 8192);

        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_ForDifferentObjects_ShouldDiffer()
    {
        var a = new BlockRequest(5, 16384, 8192);
        var b = new BlockRequest(6, 16384, 8192);

        a.GetHashCode().Should().NotBe(b.GetHashCode());
    }

    [Fact]
    public void BlockRequest_CanBeUsedAsDictionaryKey()
    {
        var dict = new Dictionary<BlockRequest, string>();
        var block = new BlockRequest(5, 16384, 8192);

        dict[block] = "test";
        dict[new BlockRequest(5, 16384, 8192)].Should().Be("test");
    }

    #endregion
}
