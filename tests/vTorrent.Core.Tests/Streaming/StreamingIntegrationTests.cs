using FluentAssertions;
using vTorrent.Core.Streaming;
using Xunit;

namespace vTorrent.Tests.Core.Streaming;

public class StreamingIntegrationTests
{
    [Fact]
    public void StreamingManager_FullLifecycle()
    {
        var mgr = new StreamingManager(1000);

        // Set deadlines for pieces 0-4 (simulating a video player buffering)
        for (int i = 0; i < 5; i++)
            mgr.SetPieceDeadline(i, (i + 1) * 500);

        mgr.HasDeadlines.Should().BeTrue();

        // Get pieces — should be sorted by deadline (piece 0 first)
        var critical = mgr.GetTimeCriticalPieces(idx => false);
        critical.Should().HaveCount(5);
        critical[0].PieceIndex.Should().Be(0);
        critical[4].PieceIndex.Should().Be(4);

        // Simulate piece 0 arriving — should return true (was time-critical)
        var wasTimeCritical = mgr.OnPieceCompleted(0);
        wasTimeCritical.Should().BeTrue();

        critical = mgr.GetTimeCriticalPieces(idx => false);
        critical.Should().HaveCount(4);
        critical[0].PieceIndex.Should().Be(1);

        // Non-deadline piece completion — should return false
        var wasNotCritical = mgr.OnPieceCompleted(99);
        wasNotCritical.Should().BeFalse();

        // Clear all
        mgr.ClearPieceDeadlines();
        mgr.HasDeadlines.Should().BeFalse();
    }

    [Fact]
    public void StreamingManager_DeadlineUpdate_MaintainsSortOrder()
    {
        var mgr = new StreamingManager(100);

        mgr.SetPieceDeadline(1, 3000);
        mgr.SetPieceDeadline(2, 1000);
        mgr.SetPieceDeadline(3, 2000);

        // Update piece 1 to be most urgent
        mgr.SetPieceDeadline(1, 100);

        var pieces = mgr.GetTimeCriticalPieces(idx => false);
        pieces[0].PieceIndex.Should().Be(1);
    }

    [Fact]
    public void StreamingManager_AlertFiredOnlyForFlaggedPieces()
    {
        var mgr = new StreamingManager(100);
        var alerts = new List<int>();
        mgr.PieceAvailable += idx => alerts.Add(idx);

        mgr.SetPieceDeadline(1, 1000, alertWhenAvailable: true);
        mgr.SetPieceDeadline(2, 1000, alertWhenAvailable: false);
        mgr.SetPieceDeadline(3, 1000, alertWhenAvailable: true);

        mgr.OnPieceCompleted(1);
        mgr.OnPieceCompleted(2);
        mgr.OnPieceCompleted(3);

        alerts.Should().BeEquivalentTo(new[] { 1, 3 });
    }

    [Fact]
    public void AutoSequential_IntegratesWithManualSequential()
    {
        AutoSequentialDetector.ShouldEnable(15, 1).Should().BeTrue();
        AutoSequentialDetector.ShouldEnable(5, 1).Should().BeFalse();
    }
}
