using System.Net;
using FluentAssertions;
using vTorrent.Core.Network.Proxy;
using Xunit;

namespace vTorrent.Core.Tests.Network.Proxy;

public class Socks4ProxyConnectorTests
{
    [Fact]
    public void EncodeConnectRequest_CorrectFormat()
    {
        var ip = IPAddress.Parse("192.168.1.1");
        var result = Socks4ProxyConnector.EncodeConnectRequest(ip, 6881, "user1");

        result[0].Should().Be(0x04, "VN must be 0x04");
        result[1].Should().Be(0x01, "CD must be 0x01 (CONNECT)");
        // Port 6881 = 0x1AE1 big-endian
        result[2].Should().Be(0x1A, "port high byte");
        result[3].Should().Be(0xE1, "port low byte");
        // IP 192.168.1.1
        result[4].Should().Be(192);
        result[5].Should().Be(168);
        result[6].Should().Be(1);
        result[7].Should().Be(1);
        // userId "user1" = 5 bytes
        result[8].Should().Be((byte)'u');
        result[9].Should().Be((byte)'s');
        result[10].Should().Be((byte)'e');
        result[11].Should().Be((byte)'r');
        result[12].Should().Be((byte)'1');
        // Null terminator
        result[13].Should().Be(0x00);
        result.Should().HaveCount(14);
    }

    [Fact]
    public void EncodeConnectRequest_EmptyUserId_HasNullTerminator()
    {
        var ip = IPAddress.Parse("10.0.0.1");
        var result = Socks4ProxyConnector.EncodeConnectRequest(ip, 80);

        result.Should().HaveCount(9, "8 header bytes + 1 null terminator");
        result[8].Should().Be(0x00, "null terminator for empty userId");
    }

    [Fact]
    public void EncodeConnectRequest_PortEncoding_BigEndian()
    {
        var ip = IPAddress.Parse("127.0.0.1");

        // Port 256 = 0x0100
        var result = Socks4ProxyConnector.EncodeConnectRequest(ip, 256);
        result[2].Should().Be(0x01);
        result[3].Should().Be(0x00);

        // Port 1 = 0x0001
        result = Socks4ProxyConnector.EncodeConnectRequest(ip, 1);
        result[2].Should().Be(0x00);
        result[3].Should().Be(0x01);

        // Port 65535 = 0xFFFF
        result = Socks4ProxyConnector.EncodeConnectRequest(ip, 65535);
        result[2].Should().Be(0xFF);
        result[3].Should().Be(0xFF);
    }
}
