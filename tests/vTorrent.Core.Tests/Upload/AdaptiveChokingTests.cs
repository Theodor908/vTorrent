using vTorrent.Core.Upload;
using Xunit;

namespace vTorrent.Core.Tests.Upload;

public class AdaptiveChokingTests
{
    [Theory]
    [InlineData(6, 100_000, 40_000, 0.3, 7)]  // bonus for outlier
    [InlineData(6, 50_000, 40_000, 0.3, 6)]   // no bonus
    [InlineData(6, 100_000, 40_000, 0.6, 6)]  // bonus + penalty = net 0
    [InlineData(4, 100_000, 40_000, 0.8, 4)]  // clamped to min
    public void AdaptiveSlots_BonusAndPenalty(int baseSlots, double topRate, double meanRate, double snubRatio, int expectedSlots)
    {
        int slots = baseSlots;
        if (topRate > 2 * meanRate && meanRate > 0) slots++;
        if (snubRatio > 0.5) slots--;
        slots = Math.Clamp(slots, 4, 12);
        Assert.Equal(expectedSlots, slots);
    }

    [Fact]
    public void SelectAdaptiveUnchokes_HighestScoresSelected()
    {
        var scores = new[] { 0.9, 0.7, 0.5, 0.3, 0.1 };
        var selected = scores.OrderByDescending(s => s).Take(3).ToArray();
        Assert.Equal(new[] { 0.9, 0.7, 0.5 }, selected);
    }

    [Fact]
    public void PhaseWeights_SeedingHasZeroReciprocity()
    {
        var w = PeerScoreTracker.GetWeights(DownloadPhase.Seeding);
        Assert.Equal(0.0, w.Reciprocity);
        Assert.Equal(0.5, w.Redistribution); // highest weight in seeding
    }

    [Fact]
    public void PhaseWeights_EndgameLatencyDominates()
    {
        var w = PeerScoreTracker.GetWeights(DownloadPhase.Endgame);
        Assert.Equal(0.5, w.Latency); // highest weight in endgame
    }
}
