using System.Net;
using System.Net.Http;
using FluentAssertions;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Network.Proxy;
using Xunit;

namespace vTorrent.Core.Tests.Network.Proxy;

/// <summary>
/// Verifies that <see cref="ProxyHttpHandlerConfigurator"/> wires a SocketsHttpHandler for the
/// configured proxy so engine-level HTTP clients (web seeds / HTTP seeds) tunnel through the
/// proxy instead of leaking direct. Mirrors the tracker factory's proxy handling.
/// </summary>
public class ProxyHttpHandlerConfiguratorTests
{
    private static SocketsHttpHandler NewHandler() => new();

    [Fact]
    public void Socks5_Enabled_SetsConnectCallbackAndDisablesNativeProxy()
    {
        var settings = new ProxySettings
        {
            Type = ProxyType.Socks5, Hostname = "127.0.0.1", Port = 1080
        };
        var handler = NewHandler();

        ProxyHttpHandlerConfigurator.Configure(handler, settings, enabledForConnectionType: true);

        // SOCKS is tunneled via ConnectCallback, not the native IWebProxy path.
        handler.UseProxy.Should().BeFalse();
        handler.ConnectCallback.Should().NotBeNull();
    }

    [Fact]
    public void HttpProxy_Enabled_SetsWebProxy()
    {
        var settings = new ProxySettings
        {
            Type = ProxyType.Http, Hostname = "proxy.example.com", Port = 8080
        };
        var handler = NewHandler();

        ProxyHttpHandlerConfigurator.Configure(handler, settings, enabledForConnectionType: true);

        handler.UseProxy.Should().BeTrue();
        handler.Proxy.Should().BeOfType<WebProxy>();
        handler.ConnectCallback.Should().BeNull();
    }

    [Fact]
    public void HttpPassword_Enabled_SetsWebProxyWithCredentials()
    {
        var settings = new ProxySettings
        {
            Type = ProxyType.HttpPassword, Hostname = "proxy.example.com", Port = 8080,
            Username = "u", Password = "p"
        };
        var handler = NewHandler();

        ProxyHttpHandlerConfigurator.Configure(handler, settings, enabledForConnectionType: true);

        handler.UseProxy.Should().BeTrue();
        var proxy = handler.Proxy.Should().BeOfType<WebProxy>().Subject;
        proxy.Credentials.Should().NotBeNull();
    }

    [Fact]
    public void NoneType_LeavesHandlerDirect()
    {
        var settings = new ProxySettings { Type = ProxyType.None };
        var handler = NewHandler();

        ProxyHttpHandlerConfigurator.Configure(handler, settings, enabledForConnectionType: true);

        handler.UseProxy.Should().BeFalse();
        handler.ConnectCallback.Should().BeNull();
    }

    [Fact]
    public void GateDisabled_LeavesHandlerDirect_EvenWithSocksConfigured()
    {
        // A SOCKS proxy is configured, but this connection category is opted out
        // (e.g. ProxyPeerConnections == false) — traffic must go direct.
        var settings = new ProxySettings
        {
            Type = ProxyType.Socks5, Hostname = "127.0.0.1", Port = 1080
        };
        var handler = NewHandler();

        ProxyHttpHandlerConfigurator.Configure(handler, settings, enabledForConnectionType: false);

        handler.UseProxy.Should().BeFalse();
        handler.ConnectCallback.Should().BeNull();
    }

    [Fact]
    public void NullSettings_LeavesHandlerDirect()
    {
        var handler = NewHandler();

        ProxyHttpHandlerConfigurator.Configure(handler, settings: null, enabledForConnectionType: true);

        handler.UseProxy.Should().BeFalse();
        handler.ConnectCallback.Should().BeNull();
    }
}
