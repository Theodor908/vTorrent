// tests/vTorrent.CLI.Tests/Output/HumanUnitsTests.cs
using FluentAssertions;
using Xunit;
using vTorrent.Cli.Output;

namespace vTorrent.Cli.Tests.Output;

public class HumanUnitsTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(1048576, "1.00 MB")]
    [InlineData(1073741824, "1.00 GB")]
    [InlineData(2426847232L, "2.26 GB")]
    public void FormatBytes_ReturnsHumanReadable(long bytes, string expected)
    {
        HumanUnits.FormatBytes(bytes).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, "-")]
    [InlineData(1024, "1.00 KB/s")]
    [InlineData(2516582, "2.40 MB/s")]
    public void FormatSpeed_ReturnsHumanReadable(int bytesPerSec, string expected)
    {
        HumanUnits.FormatSpeed(bytesPerSec).Should().Be(expected);
    }

    [Fact]
    public void FormatDuration_ShortDurations()
    {
        HumanUnits.FormatDuration(TimeSpan.FromSeconds(45)).Should().Be("45s");
        HumanUnits.FormatDuration(TimeSpan.FromMinutes(5)).Should().Be("5m 0s");
        HumanUnits.FormatDuration(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(12)).Should().Be("3h 12m");
    }

    [Fact]
    public void FormatDuration_LongDurations()
    {
        HumanUnits.FormatDuration(TimeSpan.FromDays(2) + TimeSpan.FromHours(5)).Should().Be("2d 5h");
    }

    [Theory]
    [InlineData(0.0, "0.00")]
    [InlineData(1.234, "1.23")]
    [InlineData(0.5, "0.50")]
    public void FormatRatio_ReturnsFormattedRatio(double ratio, string expected)
    {
        HumanUnits.FormatRatio(ratio).Should().Be(expected);
    }

    [Fact]
    public void FormatProgress_ReturnsPercentage()
    {
        HumanUnits.FormatProgress(0.0).Should().Be("0%");
        HumanUnits.FormatProgress(0.456).Should().Be("45%");
        HumanUnits.FormatProgress(1.0).Should().Be("100%");
    }
}
