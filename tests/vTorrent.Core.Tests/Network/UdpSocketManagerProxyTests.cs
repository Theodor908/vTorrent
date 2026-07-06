using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Network;
using Xunit;

namespace vTorrent.Tests.Unit.Core.Network;

public class UdpSocketManagerProxyTests
{
    private sealed class RecordingSendSink : IUdpSendSink
    {
        public List<byte[]> Sends { get; } = new();
        public void Send(ReadOnlySpan<byte> data, IPEndPoint target) => Sends.Add(data.ToArray());
    }

    [Fact]
    public void SendAsync_ProxyRequiredButAssociationInactive_DropsInsteadOfSendingDirect()
    {
        using var manager = new UdpSocketManager();
        var sink = new RecordingSendSink();
        var settings = new ProxySettings { Type = ProxyType.Socks5, ProxyDht = true };
        manager.ConfigureProxyRoutingForTest(settings, sink, association: null);

        var result = manager.SendAsync(
            new byte[] { 1, 2, 3 }, new IPEndPoint(IPAddress.Loopback, 6881), UdpSendFlags.None);

        result.IsCompleted.Should().BeTrue();
        sink.Sends.Should().BeEmpty(
            "fail-closed: proxied DHT must not go direct when the SOCKS5 association is inactive");
    }

    [Fact]
    public void SendAsync_ProxyNotRequired_SendsDirectViaSink()
    {
        using var manager = new UdpSocketManager();
        var sink = new RecordingSendSink();
        var settings = new ProxySettings { Type = ProxyType.None };
        manager.ConfigureProxyRoutingForTest(settings, sink);

        manager.SendAsync(new byte[] { 9 }, new IPEndPoint(IPAddress.Loopback, 6881), UdpSendFlags.None);

        sink.Sends.Should().HaveCount(1, "non-proxied traffic sends direct through the sink");
    }


    [Theory]
    [InlineData(UdpSendFlags.None, true, true, true, true)]
    [InlineData(UdpSendFlags.None, false, true, true, false)]
    [InlineData(UdpSendFlags.PeerConnection, true, true, true, true)]
    [InlineData(UdpSendFlags.PeerConnection, true, false, true, false)]
    [InlineData(UdpSendFlags.TrackerConnection, true, true, true, true)]
    [InlineData(UdpSendFlags.TrackerConnection, true, true, false, false)]
    public void ShouldUseProxy_RespectsFlags(UdpSendFlags flags, bool proxyDht,
        bool proxyPeers, bool proxyTrackers, bool expected)
    {
        var settings = new ProxySettings
        {
            Type = ProxyType.Socks5,
            ProxyDht = proxyDht,
            ProxyPeerConnections = proxyPeers,
            ProxyTrackerConnections = proxyTrackers
        };

        UdpSocketManager.ShouldUseProxy(flags, settings).Should().Be(expected);
    }

    [Fact]
    public void ShouldUseProxy_NoProxy_AlwaysFalse()
    {
        var settings = new ProxySettings { Type = ProxyType.None };

        UdpSocketManager.ShouldUseProxy(UdpSendFlags.None, settings).Should().BeFalse();
        UdpSocketManager.ShouldUseProxy(UdpSendFlags.PeerConnection, settings).Should().BeFalse();
    }

    [Fact]
    public void ShouldUseProxy_HttpProxy_FalseForUdp()
    {
        var settings = new ProxySettings
        {
            Type = ProxyType.Http,
            ProxyDht = true
        };

        UdpSocketManager.ShouldUseProxy(UdpSendFlags.None, settings).Should().BeFalse();
    }
}
