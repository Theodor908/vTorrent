using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.Upload;
using vTorrent.Tests.Mocks;
using Xunit;

namespace vTorrent.Tests.Unit;

public class SuperSeedManagerTests
{
    private const int TotalPieces = 10;

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SuperSeedManager CreateManager(
        int totalPieces,
        List<IPeerConnection>? peers = null)
    {
        peers ??= new List<IPeerConnection>();
        var peerManagerMock = MockFactories.CreatePeerManagerMock(peers);
        var logger = new Mock<ILogger<SuperSeedManager>>().Object;
        return new SuperSeedManager(totalPieces, peerManagerMock.Object, logger);
    }

    /// <summary>
    /// Builds a peer bitfield (MSB-first) marking the given piece indices as already owned.
    /// </summary>
    private static byte[] BuildBitfield(int totalPieces, params int[] ownedPieces)
    {
        var bf = new byte[(totalPieces + 7) / 8];
        foreach (int piece in ownedPieces)
        {
            int byteIndex = piece / 8;
            int bitPos = 7 - (piece % 8); // MSB-first
            bf[byteIndex] |= (byte)(1 << bitPos);
        }
        return bf;
    }

    // ── Test 1: Piece selection ───────────────────────────────────────────────

    [Fact]
    public void GetPieceToSuperSeed_WhenEnabled_ReturnsValidPieceForNewPeer()
    {
        var manager = CreateManager(TotalPieces);
        manager.Enable();

        var peerMock = MockFactories.CreatePeerConnectionMock(pieceCount: TotalPieces, hasPieces: false);
        int piece = manager.GetPieceToSuperSeed(peerMock.Object, null);

        piece.Should().BeInRange(0, TotalPieces - 1);
    }

    // ── Test 2: Rarest-first ─────────────────────────────────────────────────

    [Fact]
    public void GetPieceToSuperSeed_RarestFirst_SelectsPieceWithLowestSuperSeedCount()
    {
        // 3 pieces: piece 0 has count 1, pieces 1 and 2 have count 0.
        // Rarest-first should choose piece 1 or 2 (not piece 0).
        // We use 3 pieces to keep the test deterministic about what counts look like.
        const int pieces = 3;
        var manager = CreateManager(pieces);
        manager.Enable();

        var peer1Mock = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);
        var peer2Mock = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);

        // Assign piece 0 to peer1 — it now has superSeedCount[0] == 1.
        // We force the assignment by building a bitfield that has pieces 1 and 2 so piece 0 is the only candidate.
        var bf1 = BuildBitfield(pieces, 1, 2); // peer1 already owns 1 and 2
        int assignedToPeer1 = manager.GetPieceToSuperSeed(peer1Mock.Object, bf1);
        assignedToPeer1.Should().Be(0);

        // Now ask for peer2 with empty bitfield — count[0]=1, count[1]=0, count[2]=0.
        // Rarest-first should pick 1 or 2 (count 0) before piece 0 (count 1).
        int assignedToPeer2 = manager.GetPieceToSuperSeed(peer2Mock.Object, null);
        assignedToPeer2.Should().NotBe(0, "piece 0 has a higher super-seed count");
    }

    // ── Test 3: Duplicate avoidance ───────────────────────────────────────────

    [Fact]
    public void GetPieceToSuperSeed_DuplicateAvoidance_PrefersZeroCountPieces()
    {
        // With 5 pieces, assign piece 0 to two different peers.
        // The third peer should receive a piece that still has count 0.
        const int pieces = 5;
        var manager = CreateManager(pieces);
        manager.Enable();

        var peer1Mock = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);
        var peer2Mock = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);
        var peer3Mock = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);

        // Force both peer1 and peer2 to get piece 0 by excluding all others.
        var bfExclude1to4 = BuildBitfield(pieces, 1, 2, 3, 4);
        manager.GetPieceToSuperSeed(peer1Mock.Object, bfExclude1to4);
        manager.GetPieceToSuperSeed(peer2Mock.Object, bfExclude1to4);
        // superSeedCount[0] == 2, all others == 0.

        // peer3 has empty bitfield — should receive a piece with count 0.
        int assignedToPeer3 = manager.GetPieceToSuperSeed(peer3Mock.Object, null);
        assignedToPeer3.Should().BeInRange(1, 4,
            "pieces 1-4 have super-seed count 0, which is rarer than piece 0 with count 2");
    }

    // ── Test 4: Propagation gating (per-slot) ────────────────────────────────

    [Fact]
    public void GetPieceToSuperSeed_WhenAllPiecesOwnedOrInSlots_ReturnsMinusOne()
    {
        // Use 3 pieces. Assign slot 0 to piece 0, slot 1 to piece 1 for the peer.
        // Then pass a bitfield indicating the peer also owns piece 2.
        // No candidates remain → should return -1.
        const int pieces = 3;
        var manager = CreateManager(pieces);
        manager.Enable();

        var peerMock = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);
        var peer = peerMock.Object;

        // Slot 0: restrict to only piece 0 by passing bitfield owning pieces 1 and 2.
        var bfOwns1and2 = BuildBitfield(pieces, 1, 2);
        int first = manager.GetPieceToSuperSeed(peer, bfOwns1and2);
        first.Should().Be(0, "pieces 1 and 2 are owned; piece 0 is the only candidate");

        // Slot 1: restrict to only piece 1 by passing bitfield owning pieces 0 and 2.
        // (Piece 0 is already in slot 0, piece 2 is owned by peer, so piece 1 is the only candidate.)
        var bfOwns0and2 = BuildBitfield(pieces, 0, 2);
        int second = manager.GetPieceToSuperSeed(peer, bfOwns0and2);
        second.Should().Be(1, "piece 1 is the only remaining candidate for slot 1");

        // Now both slots are assigned (piece 0 and piece 1). Pass a bitfield where the peer
        // also owns piece 2 — all three pieces are either in slots or owned → -1.
        var bfOwns2 = BuildBitfield(pieces, 2);
        // (Pieces 0 and 1 are already in slots; piece 2 is in the bitfield.)
        int third = manager.GetPieceToSuperSeed(peer, bfOwns2);
        third.Should().Be(-1, "all pieces are either already in a slot or owned by the peer");
    }

    // ── Test 5: Propagation satisfied ────────────────────────────────────────

    [Fact]
    public void OnHaveReceived_FromDifferentPeer_MarksPropagatedAndReturnsReveal()
    {
        const int pieces = 5;
        var manager = CreateManager(pieces);
        manager.Enable();

        var seededPeerMock = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);
        var otherPeerMock  = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);
        var seededPeer = seededPeerMock.Object;
        var otherPeer  = otherPeerMock.Object;

        // Give seededPeer a piece in slot 0.
        int assignedPiece = manager.GetPieceToSuperSeed(seededPeer, null);
        assignedPiece.Should().BeGreaterThanOrEqualTo(0);

        // Fill slot 1 as well so we can verify OnHaveReceived only triggers for the right slot.
        var bfOwnsFirst = BuildBitfield(pieces, assignedPiece);
        manager.GetPieceToSuperSeed(seededPeer, bfOwnsFirst);

        // otherPeer reports having the piece — this should mark slot propagated
        // and return a new piece to reveal to seededPeer.
        var reveals = manager.OnHaveReceived(otherPeer, assignedPiece);

        reveals.Should().NotBeNull();
        reveals!.Should().ContainSingle(r => r.Peer == seededPeer,
            "seededPeer had that piece in a slot and it just got propagated");
        reveals[0].NewPiece.Should().BeGreaterThanOrEqualTo(0);
    }

    // ── Test 6: Same-peer HAVE ignored ───────────────────────────────────────

    [Fact]
    public void OnHaveReceived_FromSamePeer_DoesNotCountAsPropagation()
    {
        const int pieces = 5;
        var manager = CreateManager(pieces);
        manager.Enable();

        var peerMock = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);
        var peer = peerMock.Object;

        int assignedPiece = manager.GetPieceToSuperSeed(peer, null);
        assignedPiece.Should().BeGreaterThanOrEqualTo(0);

        // The same peer sends HAVE for its own assigned piece — should not propagate.
        var reveals = manager.OnHaveReceived(peer, assignedPiece);

        // Reveals should be null or empty — no propagation happened.
        (reveals == null || reveals.Count == 0).Should().BeTrue(
            "the HAVE sender is the same peer that was assigned the piece; it must not count");
    }

    // ── Test 7: Single peer still works ──────────────────────────────────────

    [Fact]
    public void GetPieceToSuperSeed_SinglePeer_ReturnsValidPiece()
    {
        var manager = CreateManager(TotalPieces);
        manager.Enable();

        var peerMock = MockFactories.CreatePeerConnectionMock(pieceCount: TotalPieces);
        int piece = manager.GetPieceToSuperSeed(peerMock.Object, null);

        piece.Should().BeInRange(0, TotalPieces - 1,
            "single-peer swarm should still receive a piece assignment");
    }

    [Fact]
    public void OnHaveReceived_SinglePeer_NoRevealsBecauseSameAsFromPeer()
    {
        var manager = CreateManager(TotalPieces);
        manager.Enable();

        var peerMock = MockFactories.CreatePeerConnectionMock(pieceCount: TotalPieces);
        var peer = peerMock.Object;

        int assigned = manager.GetPieceToSuperSeed(peer, null);

        // With only one tracked peer the only state entry IS fromPeer, so no reveals.
        var reveals = manager.OnHaveReceived(peer, assigned);
        (reveals == null || reveals.Count == 0).Should().BeTrue();
    }

    // ── Test 8: DisableAsync sends HAVEs for unseen pieces ───────────────────

    [Fact]
    public async Task DisableAsync_SendsAnnounceHaveForAllUnknownPieces()
    {
        // Use 4 pieces. Force the single assigned piece by giving the peer a bitfield
        // that owns pieces 1, 2, 3 — leaving only piece 0 as a candidate.
        const int pieces = 4;
        var peerMock = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);
        var peer = peerMock.Object;

        var peers = new List<IPeerConnection> { peer };
        var peerManagerMock = MockFactories.CreatePeerManagerMock(peers);
        var logger = new Mock<ILogger<SuperSeedManager>>().Object;
        var manager = new SuperSeedManager(pieces, peerManagerMock.Object, logger);

        manager.Enable();

        // Restrict candidates to piece 0 only — this is deterministic regardless of random offset.
        var bfOwns1to3 = BuildBitfield(pieces, 1, 2, 3);
        int assignedPiece = manager.GetPieceToSuperSeed(peer, bfOwns1to3);
        assignedPiece.Should().Be(0, "pieces 1-3 are owned by the peer; piece 0 is the only candidate");

        await manager.DisableAsync();

        // Pieces 1, 2, 3 were never shown to the peer — each should have been announced.
        peerMock.Verify(x => x.AnnounceHaveAsync(1, It.IsAny<System.Threading.CancellationToken>()), Times.Once, "piece 1 was not shown");
        peerMock.Verify(x => x.AnnounceHaveAsync(2, It.IsAny<System.Threading.CancellationToken>()), Times.Once, "piece 2 was not shown");
        peerMock.Verify(x => x.AnnounceHaveAsync(3, It.IsAny<System.Threading.CancellationToken>()), Times.Once, "piece 3 was not shown");

        // The assigned piece (0) must NOT have been re-announced.
        peerMock.Verify(
            x => x.AnnounceHaveAsync(0, It.IsAny<System.Threading.CancellationToken>()),
            Times.Never,
            "piece 0 was shown to the peer and must not be re-announced");
    }

    // ── Test 9: Enable/disable toggle ────────────────────────────────────────

    [Fact]
    public void IsEnabled_ReflectsCurrentState()
    {
        var manager = CreateManager(TotalPieces);

        manager.IsEnabled.Should().BeFalse("manager starts disabled");

        manager.Enable();
        manager.IsEnabled.Should().BeTrue("manager was just enabled");
    }

    [Fact]
    public async Task IsEnabled_AfterDisableAsync_IsFalse()
    {
        var manager = CreateManager(TotalPieces);
        manager.Enable();
        manager.IsEnabled.Should().BeTrue();

        await manager.DisableAsync();
        manager.IsEnabled.Should().BeFalse();
    }

    // ── Test 10: Peer disconnect cleanup ─────────────────────────────────────

    [Fact]
    public void OnPeerDisconnected_RemovesStateAndDecrementsCount()
    {
        const int pieces = 5;
        var manager = CreateManager(pieces);
        manager.Enable();

        var peerMock1 = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);
        var peerMock2 = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);
        var peer1 = peerMock1.Object;
        var peer2 = peerMock2.Object;

        // Assign a piece to peer1 (forces count to 1 for that piece).
        int assignedToPeer1 = manager.GetPieceToSuperSeed(peer1, null);
        assignedToPeer1.Should().BeGreaterThanOrEqualTo(0);

        // Disconnect peer1 — count for that piece should go back to 0.
        manager.OnPeerDisconnected(peer1);

        // Now peer2 should get a piece with count 0.  Because peer1's piece count was
        // decremented, the previously-assigned piece is again available at count 0.
        // Build a bitfield for peer2 that makes the previously assigned piece the ONLY
        // option to verify the count truly returned to 0 and peer2 can get it.
        var bfExcludeAll = BuildBitfield(pieces, Enumerable.Range(0, pieces)
            .Where(p => p != assignedToPeer1).ToArray());
        int assignedToPeer2 = manager.GetPieceToSuperSeed(peer2, bfExcludeAll);
        assignedToPeer2.Should().Be(assignedToPeer1,
            "the disconnected peer's piece count was decremented back to 0 and is now a valid candidate again");
    }

    [Fact]
    public void OnPeerDisconnected_UnknownPeer_DoesNotThrow()
    {
        var manager = CreateManager(TotalPieces);
        manager.Enable();

        var peerMock = MockFactories.CreatePeerConnectionMock(pieceCount: TotalPieces);

        // Peer was never registered — disconnect should be a no-op.
        var act = () => manager.OnPeerDisconnected(peerMock.Object);
        act.Should().NotThrow();
    }

    // ── Test 11: CreateBitfieldProvider ──────────────────────────────────────

    [Fact]
    public void CreateBitfieldProvider_WhenEnabled_ReturnsNull()
    {
        var manager = CreateManager(TotalPieces);
        manager.Enable();

        var originalBitfield = new byte[] { 0xFF };
        var provider = manager.CreateBitfieldProvider(() => originalBitfield);

        provider().Should().BeNull("super-seeding advertises HAVE_NONE, not the real bitfield");
    }

    [Fact]
    public void CreateBitfieldProvider_WhenDisabled_ReturnsOriginalBitfield()
    {
        var manager = CreateManager(TotalPieces);
        // Do NOT call Enable() — manager stays disabled.

        var originalBitfield = new byte[] { 0xAB };
        var provider = manager.CreateBitfieldProvider(() => originalBitfield);

        provider().Should().BeSameAs(originalBitfield);
    }

    // ── Test 12: Not enabled ─────────────────────────────────────────────────

    [Fact]
    public void OnHaveReceived_WhenNotEnabled_ReturnsNull()
    {
        var manager = CreateManager(TotalPieces);
        // Do NOT enable.

        var peerMock = MockFactories.CreatePeerConnectionMock(pieceCount: TotalPieces);
        var result = manager.OnHaveReceived(peerMock.Object, 0);

        result.Should().BeNull("the manager is disabled; it should not process HAVE messages");
    }

    [Fact]
    public void GetPieceToSuperSeed_WhenNotEnabled_ReturnsMinusOne()
    {
        var manager = CreateManager(TotalPieces);
        // Do NOT enable.

        var peerMock = MockFactories.CreatePeerConnectionMock(pieceCount: TotalPieces);
        int piece = manager.GetPieceToSuperSeed(peerMock.Object, null);

        piece.Should().Be(-1, "the manager is disabled; no piece should be assigned");
    }

    // ── Test 13: HasPieceInBitfield MSB-first bit ordering ───────────────────

    [Fact]
    public void GetPieceToSuperSeed_SkipsPiecesAlreadySetInPeerBitfield_MsbFirst()
    {
        // With piece 0 marked in the bitfield (bit 7 of byte 0 = 0x80), the manager
        // must skip it and return piece 1 or higher.
        const int pieces = 4;
        var manager = CreateManager(pieces);
        manager.Enable();

        // Piece 0 = bit 7 of byte 0 (MSB-first).
        var bitfield = new byte[] { 0x80 }; // only piece 0 set

        var peerMock = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);
        int piece = manager.GetPieceToSuperSeed(peerMock.Object, bitfield);

        piece.Should().BeInRange(1, pieces - 1, "piece 0 is already owned; it must be skipped");
    }

    [Fact]
    public void GetPieceToSuperSeed_SkipsPiece7_WhenLsbOfFirstByteIsSet()
    {
        // Piece 7 = bit 0 of byte 0 (LSB of the first byte in MSB-first ordering).
        const int pieces = 8;
        var manager = CreateManager(pieces);
        manager.Enable();

        // 0x01 = 0000_0001 → only piece 7 is set in MSB-first ordering.
        var bitfield = new byte[] { 0x01 };

        var peerMock = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);
        int piece = manager.GetPieceToSuperSeed(peerMock.Object, bitfield);

        piece.Should().NotBe(7, "piece 7 is marked in the bitfield and must be skipped");
        piece.Should().BeInRange(0, 6);
    }

    [Fact]
    public void GetPieceToSuperSeed_SkipsPiece8_WhenMsbOfSecondByteIsSet()
    {
        // Piece 8 = bit 7 of byte 1 in MSB-first ordering.
        const int pieces = 16;
        var manager = CreateManager(pieces);
        manager.Enable();

        // byte 0 = 0x00 (pieces 0-7 not owned), byte 1 = 0x80 (piece 8 set).
        var bitfield = new byte[] { 0x00, 0x80 };

        var peerMock = MockFactories.CreatePeerConnectionMock(pieceCount: pieces);
        // Restrict candidates by having peer own all except piece 8 and piece 0,
        // so we can check whether piece 8 is correctly skipped.
        var bfOwnsAll = BuildBitfield(pieces, Enumerable.Range(1, pieces - 1).ToArray());
        // bfOwnsAll has all pieces 1-15 set; piece 0 is not set.
        // The only unowned piece is piece 0.  But we want to test piece 8 skipping specifically,
        // so use the raw bitfield that only marks piece 8.
        int piece = manager.GetPieceToSuperSeed(peerMock.Object, bitfield);

        piece.Should().NotBe(8, "piece 8 is marked in the bitfield and must be skipped");
    }
}
