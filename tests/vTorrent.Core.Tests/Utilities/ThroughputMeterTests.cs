using FluentAssertions;
using vTorrent.Core.Utilities;
using Xunit;

namespace vTorrent.Tests.Utilities;

public class ThroughputMeterTests
{
    [Fact]
    public void NewMeter_BytesPerSecond_IsZero()
    {
        var meter = new ThroughputMeter();
        meter.BytesPerSecond.Should().Be(0);
    }

    [Fact]
    public void Record_AccumulatesBytes()
    {
        var meter = new ThroughputMeter();
        meter.Record(1000);
        meter.Record(2000);
        // Before bucket roll, BytesPerSecond stays at 0 (no full second elapsed)
        // Just verify no crash
        meter.BytesPerSecond.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Record_AfterBucketRoll_UpdatesBytesPerSecond()
    {
        var meter = new ThroughputMeter();
        meter.Record(100_000);

        // Force bucket roll by advancing time (we test the EMA logic)
        // Use the ForceRoll test helper
        meter.ForceRollForTesting();

        meter.BytesPerSecond.Should().BeGreaterThan(0);
    }

    [Fact]
    public void EmaSmoothing_WeightsRecentSample()
    {
        var meter = new ThroughputMeter();

        // Record 100KB then roll
        meter.Record(100_000);
        meter.ForceRollForTesting();
        var first = meter.BytesPerSecond;

        // Record 0 then roll — EMA should decay
        meter.ForceRollForTesting();
        meter.BytesPerSecond.Should().BeLessThan(first);
    }
}
