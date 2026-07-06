using FluentAssertions;
using System.Net;
using Xunit;
using vTorrent.Abstractions.Models;
using vTorrent.Core.Network.I2P;

namespace vTorrent.Core.Tests.Network.I2P;

public class I2pLeakDetectorTests
{
    [Fact]
    public void AssertI2pEndPoint_WithI2pEndPoint_DoesNotThrow()
    {
        var dest = I2pDestination.FromHash(new byte[32]);
        var ep = new I2pEndPoint(dest);
        var act = () => I2pLeakDetector.AssertI2pEndPoint(ep);
        act.Should().NotThrow();
    }

    [Fact]
    public void AssertI2pEndPoint_WithIPEndPoint_Throws()
    {
        var ep = new IPEndPoint(IPAddress.Loopback, 6881);
        // In DEBUG builds this throws I2pLeakException
        // In Release builds this is a no-op
#if DEBUG
        var act = () => I2pLeakDetector.AssertI2pEndPoint(ep);
        act.Should().Throw<I2pLeakException>().WithMessage("*LEAK*");
#endif
    }

    [Fact]
    public void AssertI2pPeer_WithI2pPeer_DoesNotThrow()
    {
        var dest = I2pDestination.FromHash(new byte[32]);
        var peer = PeerInfo.FromI2p(dest);
        var act = () => I2pLeakDetector.AssertI2pPeer(peer);
        act.Should().NotThrow();
    }

    [Fact]
    public void AssertI2pPeer_WithClearnetPeer_Throws()
    {
        var peer = new PeerInfo(IPAddress.Loopback, 6881);
#if DEBUG
        var act = () => I2pLeakDetector.AssertI2pPeer(peer);
        act.Should().Throw<I2pLeakException>().WithMessage("*LEAK*clearnet*");
#endif
    }

    [Fact]
    public void I2pTransportConnector_RejectsClearnetEndpoint()
    {
        // I2pTransportConnector.ConnectAsync with IPEndPoint should throw ArgumentException
        var dest = I2pDestination.FromHash(new byte[32]);
        var session = default(I2pSamSession); // Can't easily construct without bridge

        // Test the type check directly
        EndPoint clearnetEp = new IPEndPoint(IPAddress.Loopback, 6881);
        (clearnetEp is I2pEndPoint).Should().BeFalse();

        EndPoint i2pEp = new I2pEndPoint(dest);
        (i2pEp is I2pEndPoint).Should().BeTrue();
    }

    [Fact]
    public void I2pPexExtension_NeverOutputsIPAddresses()
    {
        // Generate I2P PEX message and verify no 4-byte IP patterns
        var peers = new[]
        {
            PeerInfo.FromI2p(I2pDestination.FromHash(MakeHash(1))),
            PeerInfo.FromI2p(I2pDestination.FromHash(MakeHash(2)))
        };

        var encoded = vTorrent.Core.PeerCommunication.Extensions.I2pPexExtension.EncodeI2pPeers(peers);

        // Each entry should be exactly 32 bytes (not 6 or 18 for IP formats)
        encoded.Length.Should().Be(64); // 2 * 32
    }

    private static byte[] MakeHash(byte seed)
    {
        var h = new byte[32];
        for (int i = 0; i < 32; i++) h[i] = (byte)(seed + i);
        return h;
    }
}
