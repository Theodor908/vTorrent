using FluentAssertions;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Network.Proxy;
using Xunit;

namespace vTorrent.Core.Tests.Network.Proxy;

public class ProxyConnectorFactoryTests
{
    [Fact]
    public void Create_None_ReturnsNull()
    {
        var settings = new ProxySettings { Type = ProxyType.None };
        ProxyConnectorFactory.Create(settings).Should().BeNull();
    }

    [Fact]
    public void Create_Socks4_ReturnsSocks4Connector()
    {
        var settings = new ProxySettings { Type = ProxyType.Socks4, Hostname = "proxy", Port = 1080 };
        ProxyConnectorFactory.Create(settings).Should().BeOfType<Socks4ProxyConnector>();
    }

    [Fact]
    public void Create_Socks5_ReturnsSocks5Connector()
    {
        var settings = new ProxySettings { Type = ProxyType.Socks5, Hostname = "proxy", Port = 1080 };
        ProxyConnectorFactory.Create(settings).Should().BeOfType<Socks5ProxyConnector>();
    }

    [Fact]
    public void Create_Socks5Password_ReturnsSocks5Connector()
    {
        var settings = new ProxySettings { Type = ProxyType.Socks5Password, Hostname = "proxy", Port = 1080 };
        ProxyConnectorFactory.Create(settings).Should().BeOfType<Socks5ProxyConnector>();
    }

    [Fact]
    public void Create_Http_ReturnsHttpConnector()
    {
        var settings = new ProxySettings { Type = ProxyType.Http, Hostname = "proxy", Port = 8080 };
        ProxyConnectorFactory.Create(settings).Should().BeOfType<HttpProxyConnector>();
    }

    [Fact]
    public void Create_HttpPassword_ReturnsHttpConnector()
    {
        var settings = new ProxySettings { Type = ProxyType.HttpPassword, Hostname = "proxy", Port = 8080 };
        ProxyConnectorFactory.Create(settings).Should().BeOfType<HttpProxyConnector>();
    }

    [Fact]
    public void Create_NullSettings_ReturnsNull()
    {
        ProxyConnectorFactory.Create(null).Should().BeNull();
    }
}
