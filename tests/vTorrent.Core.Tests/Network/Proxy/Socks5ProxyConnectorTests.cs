using System.Net;
using FluentAssertions;
using vTorrent.Core.Network.Proxy;
using Xunit;

namespace vTorrent.Core.Tests.Network.Proxy;

public class Socks5ProxyConnectorTests
{
    [Fact]
    public void EncodeGreeting_NoAuth_CorrectFormat()
    {
        var greeting = Socks5ProxyConnector.EncodeGreeting(withAuth: false);

        greeting.Should().HaveCount(3);
        greeting[0].Should().Be(0x05, "SOCKS version");
        greeting[1].Should().Be(0x01, "1 auth method offered");
        greeting[2].Should().Be(0x00, "no-auth method");
    }

    [Fact]
    public void EncodeGreeting_WithAuth_IncludesBothMethods()
    {
        var greeting = Socks5ProxyConnector.EncodeGreeting(withAuth: true);

        greeting.Should().HaveCount(4);
        greeting[0].Should().Be(0x05, "SOCKS version");
        greeting[1].Should().Be(0x02, "2 auth methods offered");
        greeting[2].Should().Be(0x00, "no-auth method");
        greeting[3].Should().Be(0x02, "username/password method");
    }

    [Fact]
    public void EncodeConnectDomain_CorrectFormat()
    {
        var request = Socks5ProxyConnector.EncodeConnectDomain("example.com", 443);

        request[0].Should().Be(0x05, "SOCKS version");
        request[1].Should().Be(0x01, "CONNECT command");
        request[2].Should().Be(0x00, "reserved");
        request[3].Should().Be(0x03, "ATYP domain name");
        request[4].Should().Be(11, "hostname length for 'example.com'");
        // Hostname bytes
        var hostnameBytes = System.Text.Encoding.ASCII.GetBytes("example.com");
        request[5..(5 + 11)].Should().BeEquivalentTo(hostnameBytes);
        // Port 443 = 0x01BB
        request[16].Should().Be(0x01);
        request[17].Should().Be(0xBB);
        request.Should().HaveCount(18);
    }

    [Fact]
    public void EncodeConnectIpv4_CorrectFormat()
    {
        var ip = IPAddress.Parse("10.20.30.40");
        var request = Socks5ProxyConnector.EncodeConnectIpv4(ip, 8080);

        request[0].Should().Be(0x05, "SOCKS version");
        request[1].Should().Be(0x01, "CONNECT command");
        request[2].Should().Be(0x00, "reserved");
        request[3].Should().Be(0x01, "ATYP IPv4");
        request[4].Should().Be(10);
        request[5].Should().Be(20);
        request[6].Should().Be(30);
        request[7].Should().Be(40);
        // Port 8080 = 0x1F90
        request[8].Should().Be(0x1F);
        request[9].Should().Be(0x90);
        request.Should().HaveCount(10);
    }

    [Fact]
    public void EncodeSuccessResponse_IPv4_CorrectFormat()
    {
        var boundAddr = IPAddress.Parse("1.2.3.4");
        var response = Socks5ProxyConnector.EncodeSuccessResponse(boundAddr, 9999);

        response[0].Should().Be(0x05, "SOCKS version");
        response[1].Should().Be(0x00, "success");
        response[2].Should().Be(0x00, "reserved");
        response[3].Should().Be(0x01, "ATYP IPv4");
        response[4].Should().Be(1);
        response[5].Should().Be(2);
        response[6].Should().Be(3);
        response[7].Should().Be(4);
        // Port 9999 = 0x270F
        response[8].Should().Be(0x27);
        response[9].Should().Be(0x0F);
        response.Should().HaveCount(10);
    }

    [Fact]
    public void EncodeConnectDomain_PortEncoding_BigEndian()
    {
        // Port 256 = 0x0100
        var request = Socks5ProxyConnector.EncodeConnectDomain("a.com", 256);
        var portOffset = 5 + 5; // 5 header + 5 hostname length
        request[portOffset].Should().Be(0x01);
        request[portOffset + 1].Should().Be(0x00);

        // Port 1 = 0x0001
        request = Socks5ProxyConnector.EncodeConnectDomain("a.com", 1);
        request[portOffset].Should().Be(0x00);
        request[portOffset + 1].Should().Be(0x01);
    }
}
