using FluentAssertions;
using vTorrent.Core.Streaming;
using Xunit;

namespace vTorrent.Tests.Core.Streaming;

public class AutoSequentialTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(5, 0, false)]
    [InlineData(9, 0, false)]
    [InlineData(10, 0, true)]
    [InlineData(10, 1, true)]
    [InlineData(10, 2, false)]
    [InlineData(100, 5, true)]
    [InlineData(100, 11, false)]
    public void ShouldEnableAutoSequential_MatchesLibtorrentLogic(
        int seeds, int downloaders, bool expected)
    {
        var result = AutoSequentialDetector.ShouldEnable(seeds, downloaders);
        result.Should().Be(expected);
    }
}
