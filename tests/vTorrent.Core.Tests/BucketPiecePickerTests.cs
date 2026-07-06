using FluentAssertions;
using vTorrent.Core;
using Xunit;
using vTorrent.Core.Download;

namespace vTorrent.Tests.Unit.Core;

public class BucketPiecePickerTests : IDisposable
{
    private BucketPiecePicker _picker;

    private BucketPiecePicker CreatePicker(int pieceCount, int[] completedPieces = null)
    {
        var picker = new BucketPiecePicker(pieceCount);
        if (completedPieces != null)
        {
            foreach (var p in completedPieces)
                picker.MarkCompleted(p);
        }
        return picker;
    }

    public void Dispose() { }

    [Fact]
    public void PickPiece_WithNoAvailability_FallbackReturnsPiece()
    {
        _picker = CreatePicker(10);
        // No availability set — all pieces have availability 0
        // Fallback picks any active piece for all-leecher swarms
        var result = _picker.PickPiece(_ => true);
        result.Should().NotBeNull("fallback picks pieces even with availability 0 for all-leecher swarms");
    }

    [Fact]
    public void PickPiece_RarestFirst_ReturnsLowestAvailability()
    {
        _picker = CreatePicker(5);
        // Piece 0: availability 3, Piece 1: availability 1, Piece 2: availability 2
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(1);
        _picker.IncrementAvailability(2);
        _picker.IncrementAvailability(2);

        var result = _picker.PickPiece(_ => true);
        result.Should().Be(1, "piece 1 is rarest with availability 1");
    }

    [Fact]
    public void PickPiece_SkipsPiecePeerDoesNotHave()
    {
        _picker = CreatePicker(5);
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(1);
        _picker.IncrementAvailability(1);

        // Peer only has piece 1
        var result = _picker.PickPiece(i => i == 1);
        result.Should().Be(1);
    }

    [Fact]
    public void PickPiece_PrefersPartialPieces()
    {
        _picker = CreatePicker(5);
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(1);

        // Both have availability 1, but piece 1 is partial
        _picker.MarkInProgress(1);

        var result = _picker.PickPiece(_ => true);
        result.Should().Be(1, "partial pieces get priority within same availability tier");
    }

    [Fact]
    public void MarkCompleted_RemovesPieceFromPicker()
    {
        _picker = CreatePicker(3);
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(1);

        _picker.MarkCompleted(0);

        var result = _picker.PickPiece(_ => true);
        result.Should().Be(1, "piece 0 was completed and removed");
    }

    [Fact]
    public void IncrementAvailability_UpdatesPriority_O1()
    {
        _picker = CreatePicker(3);
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(1);

        // Piece 0 becomes more available (less rare)
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(0);

        var result = _picker.PickPiece(_ => true);
        result.Should().Be(1, "piece 1 is now rarer than piece 0");
    }

    [Fact]
    public void DecrementAvailability_UpdatesPriority()
    {
        _picker = CreatePicker(3);
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(1);
        _picker.IncrementAvailability(1);

        // Peer with piece 0 disconnects
        _picker.DecrementAvailability(0);
        _picker.DecrementAvailability(0);

        var result = _picker.PickPiece(_ => true);
        result.Should().Be(0, "piece 0 is now rarest after decrement");
    }

    [Fact]
    public void PickPiece_Sequential_ReturnsLowestIndex()
    {
        _picker = CreatePicker(5);
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(1);
        _picker.IncrementAvailability(2);
        _picker.IncrementAvailability(3);

        // Piece 3 is rarest, but sequential mode should return piece 0
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(1);
        _picker.IncrementAvailability(2);

        var result = _picker.PickPiece(_ => true, sequential: true);
        result.Should().Be(0, "sequential mode picks lowest index regardless of availability");
    }

    [Fact]
    public void OnSeedJoined_DoesNotCauseNSwaps()
    {
        _picker = CreatePicker(1000);
        for (int i = 0; i < 1000; i++)
            _picker.IncrementAvailability(i);

        // This should be O(1), not O(n) — just sets dirty flag
        _picker.OnSeedJoined();

        // Should still pick correctly after lazy rebuild
        var result = _picker.PickPiece(_ => true);
        result.Should().NotBeNull();
    }

    [Fact]
    public void AvailablePieceCount_DecrementsOnCompletion()
    {
        _picker = CreatePicker(10);
        _picker.AvailablePieceCount.Should().Be(10);

        _picker.MarkCompleted(0);
        _picker.AvailablePieceCount.Should().Be(9);

        _picker.MarkCompleted(5);
        _picker.AvailablePieceCount.Should().Be(8);
    }

    [Fact]
    public void ApplyBitfield_BulkUpdatesAvailability()
    {
        _picker = CreatePicker(8);
        // BitTorrent bitfield: MSB of byte 0 = piece 0
        // 0b10101010 => pieces 0, 2, 4, 6 have bit set
        var bitfield = new byte[] { 0b10101010 };
        _picker.ApplyBitfield(bitfield, 8, delta: 1);

        // Piece 0 (bit 7, MSB) should be available
        var result = _picker.PickPiece(i => i == 0);
        result.Should().Be(0);

        // Piece 1 (bit 6) has availability 0 — rarest-first skips it,
        // but fallback still returns it for all-leecher swarm support
        _picker.PickPiece(i => i == 1).Should().Be(1, "fallback picks availability-0 pieces");
    }

    [Fact]
    public void PickPieceReverse_ReturnsHighestAvailabilityPiece()
    {
        _picker = CreatePicker(10);

        // Set varying availability: piece i gets availability (i+1)
        for (int i = 0; i < 10; i++)
            for (int a = 0; a <= i; a++)
                _picker.IncrementAvailability(i);

        // Reverse pick should return highest-availability piece (piece 9)
        var piece = _picker.PickPieceReverse(i => true);
        piece.Should().Be(9);
    }

    [Fact]
    public void PickPieceReverse_SkipsPiecesWithoutPeer()
    {
        _picker = CreatePicker(5);
        for (int i = 0; i < 5; i++)
            _picker.IncrementAvailability(i);

        // Peer only has piece 2
        var piece = _picker.PickPieceReverse(i => i == 2);
        piece.Should().Be(2);
    }

    [Fact]
    public void PickPieceReverse_FallbackForZeroAvailability()
    {
        // All-leecher swarm: all pieces have availability 0
        _picker = CreatePicker(5);

        var piece = _picker.PickPieceReverse(i => true);
        // Should still return a piece via fallback
        piece.Should().NotBeNull();
    }

    [Fact]
    public void MarkFinished_InProgressPiece_BecomesUnpickable()
    {
        _picker = CreatePicker(5);
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(1);
        _picker.MarkInProgress(0);

        _picker.MarkFinished(0);

        // Finished piece should not be pickable
        var result = _picker.PickPiece(i => i == 0);
        result.Should().BeNull("finished pieces are not pickable");

        // Other pieces still pickable
        var other = _picker.PickPiece(_ => true);
        other.Should().Be(1);
    }

    [Fact]
    public void MarkFinished_NotInProgress_ShouldBeIgnored()
    {
        _picker = CreatePicker(5);
        _picker.IncrementAvailability(0);

        // Piece 0 is Available, not InProgress — MarkFinished should no-op
        _picker.MarkFinished(0);

        var result = _picker.PickPiece(_ => true);
        result.Should().Be(0, "piece should still be available");
    }

    [Fact]
    public void RestorePiece_FinishedPiece_BecomesAvailableAgain()
    {
        _picker = CreatePicker(5);
        _picker.IncrementAvailability(0);
        _picker.MarkInProgress(0);
        _picker.MarkFinished(0);

        _picker.RestorePiece(0);

        var result = _picker.PickPiece(_ => true);
        result.Should().Be(0, "restored piece should be pickable again");
    }

    [Fact]
    public void RestorePiece_NotFinished_ShouldBeIgnored()
    {
        _picker = CreatePicker(5);
        _picker.IncrementAvailability(0);
        _picker.MarkInProgress(0);

        // Piece is InProgress, not Finished — RestorePiece should no-op
        _picker.RestorePiece(0);

        _picker.GetPieceState(0).Should().Be(1, "1 = InProgress, should not have changed");
    }

    [Fact]
    public void MarkCompleted_OnlyFromFinished()
    {
        _picker = CreatePicker(5);
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(1);
        _picker.MarkInProgress(0);
        _picker.MarkFinished(0);

        _picker.MarkCompleted(0);

        _picker.IsPieceCompleted(0).Should().BeTrue();
        _picker.AvailablePieceCount.Should().Be(4);
    }

    [Fact]
    public void GetPieceState_ReturnsCorrectStates()
    {
        _picker = CreatePicker(5);
        _picker.IncrementAvailability(0);
        _picker.IncrementAvailability(1);

        _picker.GetPieceState(0).Should().Be(0, "Available = 0");

        _picker.MarkInProgress(0);
        _picker.GetPieceState(0).Should().Be(1, "InProgress = 1");

        _picker.MarkFinished(0);
        _picker.GetPieceState(0).Should().Be(3, "Finished = 3");

        _picker.MarkCompleted(0);
        _picker.GetPieceState(0).Should().Be(2, "Completed = 2");
    }

    [Fact]
    public void AvailablePieceCount_FinishedPieceStillCountsAsActive()
    {
        _picker = CreatePicker(10);
        _picker.IncrementAvailability(0);
        _picker.MarkInProgress(0);

        _picker.AvailablePieceCount.Should().Be(10, "InProgress pieces are still active");

        _picker.MarkFinished(0);
        _picker.AvailablePieceCount.Should().Be(10, "Finished pieces are still active (not completed yet)");

        _picker.MarkCompleted(0);
        _picker.AvailablePieceCount.Should().Be(9, "Only MarkCompleted decrements active count");
    }
}
