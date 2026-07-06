using FluentAssertions;
using System.Net;
using Xunit;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Tests.Network.I2P;

public class I2pPeerInfoTests
{
    private readonly I2pDestination _dest;

    public I2pPeerInfoTests()
    {
        var hash = new byte[32];
        for (int i = 0; i < 32; i++) hash[i] = (byte)(i + 1);
        _dest = I2pDestination.FromHash(hash);
    }

    [Fact]
    public void FromI2p_SetsDestination()
    {
        var peer = PeerInfo.FromI2p(_dest, "tracker");
        peer.IsI2p.Should().BeTrue();
        peer.Destination.Should().Be(_dest);
    }

    [Fact]
    public void FromI2p_SetsIPAddressSentinel()
    {
        var peer = PeerInfo.FromI2p(_dest);
        peer.IpAddress.Should().Be(IPAddress.None);
        peer.Port.Should().Be(0);
    }

    [Fact]
    public void ClearnetPeer_IsNotI2p()
    {
        var peer = new PeerInfo(IPAddress.Loopback, 6881);
        peer.IsI2p.Should().BeFalse();
        peer.Destination.Should().BeNull();
    }

    [Fact]
    public void NetworkEndPoint_ReturnsI2pEndPoint_ForI2pPeer()
    {
        var peer = PeerInfo.FromI2p(_dest);
        peer.NetworkEndPoint.Should().BeOfType<I2pEndPoint>();
    }

    [Fact]
    public void NetworkEndPoint_ReturnsIPEndPoint_ForClearnetPeer()
    {
        var peer = new PeerInfo(IPAddress.Loopback, 6881);
        peer.NetworkEndPoint.Should().BeOfType<IPEndPoint>();
    }

    [Fact]
    public void DisplayAddress_ReturnsBase32Prefix_ForI2p()
    {
        var peer = PeerInfo.FromI2p(_dest);
        peer.DisplayAddress.Should().Contain("...");
        peer.DisplayAddress.Should().NotContain("0.0.0.0");
    }

    [Fact]
    public void DisplayAddress_ReturnsIPPort_ForClearnet()
    {
        var peer = new PeerInfo(IPAddress.Parse("192.168.1.1"), 6881);
        peer.DisplayAddress.Should().Be("192.168.1.1:6881");
    }

    [Fact]
    public void Equality_TwoI2pPeers_SameDestination_AreEqual()
    {
        var a = PeerInfo.FromI2p(_dest);
        var hash2 = new byte[32];
        for (int i = 0; i < 32; i++) hash2[i] = (byte)(i + 1);
        var b = PeerInfo.FromI2p(I2pDestination.FromHash(hash2));
        a.Should().Be(b);
    }

    [Fact]
    public void Equality_I2pAndClearnet_AreNotEqual()
    {
        var i2p = PeerInfo.FromI2p(_dest);
        var clearnet = new PeerInfo(IPAddress.None, 0);
        i2p.Should().NotBe(clearnet);
    }

    [Fact]
    public void IncomingI2p_CreatesI2pPeer()
    {
        var ep = new I2pEndPoint(_dest);
        var peer = PeerInfo.IncomingI2p(ep);
        peer.IsI2p.Should().BeTrue();
        peer.Source.Should().Be("incoming");
    }

    [Fact]
    public void I2pCompactFormat_RoundTrips()
    {
        var peer = PeerInfo.FromI2p(_dest, "tracker");
        var compact = peer.ToCompactFormatI2p();
        compact.Length.Should().Be(32);

        var restored = PeerInfo.FromCompactFormatI2p(compact, source: "tracker");
        restored.Destination.Should().Be(_dest);
    }
}
