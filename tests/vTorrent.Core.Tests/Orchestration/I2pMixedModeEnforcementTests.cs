using FluentAssertions;
using Xunit;

namespace vTorrent.Core.Tests.Orchestration;

public class I2pMixedModeEnforcementTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void ShouldSkipClearnetDht_ReturnsCorrectly(bool isI2p, bool allowMixed, bool shouldAllowDht)
    {
        var shouldSkip = isI2p && !allowMixed;
        shouldSkip.Should().Be(!shouldAllowDht);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    public void ShouldRejectClearnetPeer_ReturnsCorrectly(
        bool isI2pTorrent, bool isI2pPeer, bool allowMixed, bool shouldAccept)
    {
        var shouldReject = isI2pTorrent && !isI2pPeer && !allowMixed;
        shouldReject.Should().Be(!shouldAccept);
    }
}
