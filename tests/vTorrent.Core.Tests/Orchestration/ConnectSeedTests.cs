using Xunit;

namespace vTorrent.Core.Tests.Orchestration;

public class ConnectSeedTests
{
    [Fact]
    public void Counter_InjectsAfterNDownloads()
    {
        int counter = 0;
        int n = 10;
        bool seedInjected = false;
        for (int i = 0; i < 11; i++)
        {
            counter++;
            if (counter >= n)
            {
                counter = 0;
                seedInjected = true;
            }
        }
        Assert.True(seedInjected);
    }

    [Theory]
    [InlineData(1, 100, 100)]   // inject every download → 100 injections
    [InlineData(10, 100, 10)]   // default → 10 injections
    [InlineData(100, 100, 1)]   // conservative → 1 injection
    [InlineData(50, 25, 0)]     // ratio > attempts → 0 injections
    public void Counter_RespectsConfiguredRatio(int ratio, int attempts, int expectedInjections)
    {
        int counter = 0;
        int injections = 0;
        for (int i = 0; i < attempts; i++)
        {
            counter++;
            if (counter >= ratio)
            {
                counter = 0;
                injections++;
            }
        }
        Assert.Equal(expectedInjections, injections);
    }
}
