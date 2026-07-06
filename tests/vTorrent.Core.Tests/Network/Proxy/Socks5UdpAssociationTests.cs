using System;
using System.Net;
using FluentAssertions;
using vTorrent.Core.Network.Proxy;
using Xunit;

namespace vTorrent.Core.Tests.Network.Proxy;

public class Socks5UdpAssociationTests
{
    [Fact]
    public void WrapPacket_IPv4_CorrectFormat()
    {
        var target = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 6881);
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var wrapped = Socks5UdpAssociation.WrapPacket(data, target);

        wrapped[0].Should().Be(0x00); // RSV
        wrapped[1].Should().Be(0x00); // RSV
        wrapped[2].Should().Be(0x00); // FRAG
        wrapped[3].Should().Be(0x01); // ATYP = IPv4
        wrapped[4].Should().Be(1);
        wrapped[5].Should().Be(2);
        wrapped[6].Should().Be(3);
        wrapped[7].Should().Be(4);
        wrapped[8].Should().Be(0x1A); // port high
        wrapped[9].Should().Be(0xE1); // port low
        wrapped[10].Should().Be(0xDE);
        wrapped[11].Should().Be(0xAD);
        wrapped[12].Should().Be(0xBE);
        wrapped[13].Should().Be(0xEF);
    }

    [Fact]
    public void UnwrapPacket_IPv4_ExtractsCorrectly()
    {
        var target = new IPEndPoint(IPAddress.Parse("5.6.7.8"), 1234);
        var payload = new byte[] { 0x01, 0x02, 0x03 };
        var wrapped = Socks5UdpAssociation.WrapPacket(payload, target);

        var (unwrappedPayload, sender) = Socks5UdpAssociation.UnwrapPacket(wrapped);

        unwrappedPayload.Should().BeEquivalentTo(payload);
        sender.Address.Should().Be(IPAddress.Parse("5.6.7.8"));
        sender.Port.Should().Be(1234);
    }

    [Fact]
    public void WrapUnwrap_RoundTrip_Preserves()
    {
        var target = new IPEndPoint(IPAddress.Parse("10.20.30.40"), 51413);
        var data = new byte[100];
        Random.Shared.NextBytes(data);

        var wrapped = Socks5UdpAssociation.WrapPacket(data, target);
        var (unwrapped, sender) = Socks5UdpAssociation.UnwrapPacket(wrapped);

        unwrapped.Should().BeEquivalentTo(data);
        sender.Address.Should().Be(target.Address);
        sender.Port.Should().Be(target.Port);
    }

    [Fact]
    public void UnwrapPacket_TooShort_ReturnsEmpty()
    {
        var (payload, _) = Socks5UdpAssociation.UnwrapPacket(new byte[] { 0, 0, 0 });
        payload.Length.Should().Be(0);
    }

    [Fact]
    public void WrapPacket_EmptyPayload_HeaderOnly()
    {
        var target = new IPEndPoint(IPAddress.Loopback, 80);
        var wrapped = Socks5UdpAssociation.WrapPacket(ReadOnlySpan<byte>.Empty, target);
        wrapped.Length.Should().Be(10); // 3 header + 1 atyp + 4 ipv4 + 2 port
    }
}
