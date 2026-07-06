using System;
using FluentAssertions;
using vTorrent.Core.Streaming;
using Xunit;

namespace vTorrent.Tests.Core.Streaming;

public class StreamingManagerTests
{
    private const int TotalPieces = 100;

    private static StreamingManager CreateManager() => new(TotalPieces);

    [Fact]
    public void SetPieceDeadline_FirstDeadline_ReturnsTrue()
    {
        var mgr = CreateManager();
        mgr.SetPieceDeadline(0, 1000).Should().BeTrue();
    }

    [Fact]
    public void SetPieceDeadline_SecondDifferentPiece_ReturnsFalse()
    {
        var mgr = CreateManager();
        mgr.SetPieceDeadline(0, 1000);
        mgr.SetPieceDeadline(1, 2000).Should().BeFalse();
    }

    [Fact]
    public void SetPieceDeadline_UpdateExistingPiece_ReturnsFalse()
    {
        var mgr = CreateManager();
        mgr.SetPieceDeadline(5, 1000);
        mgr.SetPieceDeadline(5, 500).Should().BeFalse();
    }

    [Fact]
    public void HasDeadlines_InitiallyFalse()
    {
        var mgr = CreateManager();
        mgr.HasDeadlines.Should().BeFalse();
    }

    [Fact]
    public void HasDeadlines_TrueAfterSet()
    {
        var mgr = CreateManager();
        mgr.SetPieceDeadline(0, 1000);
        mgr.HasDeadlines.Should().BeTrue();
    }

    [Fact]
    public void GetTimeCriticalPieces_SortedByDeadline()
    {
        var mgr = CreateManager();
        // Set pieces with decreasing deadlines so insertion order != sorted order
        mgr.SetPieceDeadline(0, 3000);
        mgr.SetPieceDeadline(1, 1000);
        mgr.SetPieceDeadline(2, 2000);

        var pieces = mgr.GetTimeCriticalPieces(_ => false);
        pieces.Should().HaveCount(3);
        // Earliest deadline first
        pieces[0].PieceIndex.Should().Be(1);
        pieces[1].PieceIndex.Should().Be(2);
        pieces[2].PieceIndex.Should().Be(0);
    }

    [Fact]
    public void GetTimeCriticalPieces_ExcludesCompletedPieces()
    {
        var mgr = CreateManager();
        mgr.SetPieceDeadline(0, 1000);
        mgr.SetPieceDeadline(1, 2000);
        mgr.SetPieceDeadline(2, 3000);

        var pieces = mgr.GetTimeCriticalPieces(idx => idx == 1);
        pieces.Should().HaveCount(2);
        pieces[0].PieceIndex.Should().Be(0);
        pieces[1].PieceIndex.Should().Be(2);
    }

    [Fact]
    public void OnPieceCompleted_RemovesAndReturnsTrue_WhenTimeCritical()
    {
        var mgr = CreateManager();
        mgr.SetPieceDeadline(5, 1000);

        mgr.OnPieceCompleted(5).Should().BeTrue();
        mgr.HasDeadlines.Should().BeFalse();
    }

    [Fact]
    public void OnPieceCompleted_ReturnsFalse_WhenNotTimeCritical()
    {
        var mgr = CreateManager();
        mgr.OnPieceCompleted(5).Should().BeFalse();
    }

    [Fact]
    public void OnPieceCompleted_FiresAlert_WhenAlertFlagSet()
    {
        var mgr = CreateManager();
        mgr.SetPieceDeadline(7, 1000, alertWhenAvailable: true);

        int? alertedPiece = null;
        mgr.PieceAvailable += idx => alertedPiece = idx;

        mgr.OnPieceCompleted(7);
        alertedPiece.Should().Be(7);
    }

    [Fact]
    public void OnPieceCompleted_DoesNotFireAlert_WhenAlertFlagNotSet()
    {
        var mgr = CreateManager();
        mgr.SetPieceDeadline(7, 1000, alertWhenAvailable: false);

        bool alertFired = false;
        mgr.PieceAvailable += _ => alertFired = true;

        mgr.OnPieceCompleted(7);
        alertFired.Should().BeFalse();
    }

    [Fact]
    public void ClearPieceDeadlines_RemovesAll()
    {
        var mgr = CreateManager();
        mgr.SetPieceDeadline(0, 1000);
        mgr.SetPieceDeadline(1, 2000);

        mgr.ClearPieceDeadlines();
        mgr.HasDeadlines.Should().BeFalse();
    }

    [Fact]
    public void ResetPieceDeadline_RemovesSpecificPiece()
    {
        var mgr = CreateManager();
        mgr.SetPieceDeadline(0, 1000);
        mgr.SetPieceDeadline(1, 2000);

        mgr.ResetPieceDeadline(0);

        var pieces = mgr.GetTimeCriticalPieces(_ => false);
        pieces.Should().HaveCount(1);
        pieces[0].PieceIndex.Should().Be(1);
    }

    [Fact]
    public void SetPieceDeadline_OutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var mgr = CreateManager();

        var actNeg = () => mgr.SetPieceDeadline(-1, 1000);
        actNeg.Should().Throw<ArgumentOutOfRangeException>();

        var actHigh = () => mgr.SetPieceDeadline(TotalPieces, 1000);
        actHigh.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IncrementPeerCount_TracksPeerCount()
    {
        var mgr = CreateManager();
        mgr.SetPieceDeadline(3, 1000);

        mgr.IncrementPeerCount(3);
        mgr.IncrementPeerCount(3);

        var pieces = mgr.GetTimeCriticalPieces(_ => false);
        pieces.Should().HaveCount(1);
        pieces[0].PeerCount.Should().Be(2);
    }
}
