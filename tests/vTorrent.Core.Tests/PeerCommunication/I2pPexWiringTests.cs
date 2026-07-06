using FluentAssertions;
using vTorrent.Core.PeerCommunication.Extensions;
using Xunit;

namespace vTorrent.Core.Tests.PeerCommunication;

public class I2pPexWiringTests
{
    [Theory]
    [InlineData(false, false, false, "ut_pex")]
    [InlineData(true, true, true, "i2p_pex")]
    [InlineData(true, true, false, "i2p_pex")]
    [InlineData(true, false, true, "ut_pex")]
    public void GetPexExtensionName_ReturnsCorrectType(
        bool isI2pTorrent, bool isI2pPeer, bool allowMixedMode, string expectedName)
    {
        var result = PexRegistrationHelper.GetPexExtensionName(isI2pTorrent, isI2pPeer, allowMixedMode);
        result.Should().Be(expectedName);
    }

    [Fact]
    public void GetPexExtensionName_I2pTorrent_ClearnetPeer_NoMixed_ReturnsNull()
    {
        var result = PexRegistrationHelper.GetPexExtensionName(
            isI2pTorrent: true, isI2pPeer: false, allowMixedMode: false);
        result.Should().BeNull();
    }
}
