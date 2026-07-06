using FluentAssertions;
using Xunit;
using vTorrent.Core.Orchestration;

namespace vTorrent.Core.Tests.Network.I2P;

public class ManagedTorrentI2pDetectionTests
{
    [Fact]
    public void IsI2p_NoTrackers_ReturnsFalse()
    {
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        mt.IsI2p.Should().BeFalse();
    }

    [Fact]
    public void IsI2p_ForceI2p_ReturnsTrue()
    {
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        mt.ForceI2p = true;
        mt.IsI2p.Should().BeTrue();
    }

    [Fact]
    public void ForceI2p_DefaultsFalse()
    {
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        mt.ForceI2p.Should().BeFalse();
    }
}
