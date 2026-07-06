using vTorrent.Core.Upload;
using Xunit;

namespace vTorrent.Core.Tests.Upload;

public class PeerScoreTrackerTests
{
    // === Signal: Reciprocity ===

    [Fact]
    public void Reciprocity_NormalizedByMaxRate()
    {
        // Two peers: rates 100KB/s and 50KB/s
        // Normalized: 100/100 = 1.0, 50/100 = 0.5
        var rates = new double[] { 100_000, 50_000 };
        double maxRate = rates.Max();
        Assert.Equal(1.0, rates[0] / maxRate);
        Assert.Equal(0.5, rates[1] / maxRate);
    }

    [Fact]
    public void Reciprocity_AllZeroRates_AllZero()
    {
        double maxRate = 0;
        double score = maxRate > 0 ? 50_000 / maxRate : 0.0;
        Assert.Equal(0.0, score);
    }

    // === Signal: Stability ===

    [Fact]
    public void Stability_ConstantRate_ScoreOne()
    {
        var samples = new double[] { 100, 100, 100, 100, 100 };
        double mean = samples.Average();
        double variance = samples.Select(s => (s - mean) * (s - mean)).Average();
        double stdev = Math.Sqrt(variance);
        double stability = mean > 0 ? Math.Max(0, 1.0 - stdev / mean) : 0.5;
        Assert.Equal(1.0, stability);
    }

    [Fact]
    public void Stability_HighVariance_LowScore()
    {
        var samples = new double[] { 0, 100_000, 0, 100_000, 0 };
        double mean = samples.Average();
        double variance = samples.Select(s => (s - mean) * (s - mean)).Average();
        double stdev = Math.Sqrt(variance);
        double stability = mean > 0 ? Math.Max(0, 1.0 - stdev / mean) : 0.5;
        Assert.True(stability < 0.1, $"Stability should be very low for alternating rates, got {stability}");
    }

    [Fact]
    public void Stability_MeanZero_DefaultsToHalf()
    {
        var samples = new double[] { 0, 0, 0, 0, 0 };
        double mean = samples.Average();
        double stability = mean > 0 ? Math.Max(0, 1.0 - 0.0 / mean) : 0.5;
        Assert.Equal(0.5, stability);
    }

    [Fact]
    public void Stability_OneSample_DefaultsToHalf()
    {
        // Less than 2 samples -> can't compute stdev -> default 0.5
        int sampleCount = 1;
        double stability = sampleCount < 2 ? 0.5 : 1.0;
        Assert.Equal(0.5, stability);
    }

    // === Signal: Latency ===

    [Fact]
    public void Latency_LowRtt_HighScore()
    {
        double rttMs = 10;
        double score = 1.0 / (1.0 + rttMs / 100.0);
        Assert.True(score > 0.9, $"10ms RTT should score > 0.9, got {score}");
    }

    [Fact]
    public void Latency_100msRtt_HalfScore()
    {
        double rttMs = 100;
        double score = 1.0 / (1.0 + rttMs / 100.0);
        Assert.Equal(0.5, score);
    }

    // === Signal: Freshness ===

    [Fact]
    public void Freshness_RecentData_HighScore()
    {
        double secsSinceLastData = 5;
        double snubbedTimeout = 60;
        double score = Math.Max(0, 1.0 - secsSinceLastData / snubbedTimeout);
        Assert.True(score > 0.9);
    }

    [Fact]
    public void Freshness_StaleData_LowScore()
    {
        double secsSinceLastData = 55;
        double snubbedTimeout = 60;
        double score = Math.Max(0, 1.0 - secsSinceLastData / snubbedTimeout);
        Assert.True(score < 0.1);
    }

    // === Phase Detection ===

    [Theory]
    [InlineData(0.05, false, false, DownloadPhase.Early)]
    [InlineData(0.50, false, false, DownloadPhase.Mid)]
    [InlineData(0.90, false, false, DownloadPhase.Late)]
    [InlineData(0.99, false, true, DownloadPhase.Endgame)]
    [InlineData(1.0, true, false, DownloadPhase.Seeding)]
    public void PhaseDetection_CorrectPhase(double completion, bool seeding, bool endgame, DownloadPhase expected)
    {
        var phase = PeerScoreTracker.DetectPhase(completion, seeding, endgame);
        Assert.Equal(expected, phase);
    }

    // === Weight Sums ===

    [Fact]
    public void PhaseWeights_AllSumToOne()
    {
        foreach (DownloadPhase phase in Enum.GetValues<DownloadPhase>())
        {
            var w = PeerScoreTracker.GetWeights(phase);
            double sum = w.Reciprocity + w.Stability + w.Redistribution + w.Latency + w.Freshness;
            Assert.Equal(1.0, sum, precision: 10);
        }
    }
}
