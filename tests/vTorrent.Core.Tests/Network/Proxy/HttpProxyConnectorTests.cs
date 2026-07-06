using System;
using System.Text;
using FluentAssertions;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Network.Proxy;
using Xunit;

namespace vTorrent.Core.Tests.Network.Proxy;

public class HttpProxyConnectorTests
{
    [Fact]
    public void BuildConnectRequest_NoAuth_CorrectFormat()
    {
        var settings = new ProxySettings { Hostname = "proxy.example.com", Port = 8080 };
        var connector = new HttpProxyConnector(settings, auth: false);

        var request = connector.BuildConnectRequest("target.host", 443);

        request.Should().StartWith("CONNECT target.host:443 HTTP/1.1\r\n");
        request.Should().Contain("Host: target.host:443\r\n");
        request.Should().EndWith("\r\n\r\n");
        request.Should().NotContain("Proxy-Authorization");
    }

    [Fact]
    public void BuildConnectRequest_WithAuth_IncludesBasicAuth()
    {
        var settings = new ProxySettings
        {
            Hostname = "proxy.example.com",
            Port = 8080,
            Username = "admin",
            Password = "secret"
        };
        var connector = new HttpProxyConnector(settings, auth: true);

        var request = connector.BuildConnectRequest("target.host", 443);

        var expectedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret"));
        request.Should().Contain($"Proxy-Authorization: Basic {expectedCredentials}\r\n");
        request.Should().EndWith("\r\n\r\n");
    }

    [Fact]
    public void BuildConnectRequest_CustomPort_InRequest()
    {
        var settings = new ProxySettings { Hostname = "proxy.example.com", Port = 3128 };
        var connector = new HttpProxyConnector(settings, auth: false);

        var request = connector.BuildConnectRequest("peer.example.com", 51413);

        request.Should().StartWith("CONNECT peer.example.com:51413 HTTP/1.1\r\n");
        request.Should().Contain("Host: peer.example.com:51413\r\n");
    }
}
