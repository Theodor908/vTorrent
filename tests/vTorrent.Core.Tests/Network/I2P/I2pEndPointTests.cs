using FluentAssertions;
using System.Net;
using System.Net.Sockets;
using Xunit;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Tests.Network.I2P;

public class I2pEndPointTests
{
    private readonly I2pDestination _dest;

    public I2pEndPointTests()
    {
        var hash = new byte[32];
        for (int i = 0; i < 32; i++) hash[i] = (byte)i;
        _dest = I2pDestination.FromHash(hash);
    }

    [Fact]
    public void Constructor_StoresDestination()
    {
        var ep = new I2pEndPoint(_dest);
        ep.Destination.Should().Be(_dest);
    }

    [Fact]
    public void AddressFamily_IsNotStandardIPFamily()
    {
        var ep = new I2pEndPoint(_dest);
        ep.AddressFamily.Should().NotBe(AddressFamily.InterNetwork);
        ep.AddressFamily.Should().NotBe(AddressFamily.InterNetworkV6);
    }

    [Fact]
    public void ToString_ReturnsReadableFormat()
    {
        var ep = new I2pEndPoint(_dest);
        ep.ToString().Should().Contain("i2p:");
    }

    [Fact]
    public void Equality_SameDestination_AreEqual()
    {
        var a = new I2pEndPoint(_dest);
        var hash2 = new byte[32];
        for (int i = 0; i < 32; i++) hash2[i] = (byte)i;
        var b = new I2pEndPoint(I2pDestination.FromHash(hash2));
        a.Should().Be(b);
    }

    [Fact]
    public void IsNotIPEndPoint()
    {
        EndPoint ep = new I2pEndPoint(_dest);
        ep.Should().NotBeOfType<IPEndPoint>();
    }
}
