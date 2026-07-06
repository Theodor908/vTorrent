using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using vTorrent.Core.Network.PortMapping;

namespace vTorrent.Core.Tests.Network.PortMapping;

public class SsdpDiscoveryTests
{
    [Fact]
    public async Task Construction_AndDispose_DoesNotThrow()
    {
        await using var ssdp = new SsdpDiscovery(IPAddress.Loopback);
        // Should construct and dispose cleanly
    }

    [Fact]
    public void ParseHeader_ExtractsLocation()
    {
        var response = "HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\nLocation: http://192.168.1.1:5000/desc.xml\r\n\r\n";
        var location = SsdpDiscovery.ParseHeader(response, "Location");
        location.Should().Be("http://192.168.1.1:5000/desc.xml");
    }

    [Fact]
    public void ParseHeader_CaseInsensitive()
    {
        var response = "HTTP/1.1 200 OK\r\nlocation: http://10.0.0.1/xml\r\n\r\n";
        var location = SsdpDiscovery.ParseHeader(response, "Location");
        location.Should().Be("http://10.0.0.1/xml");
    }

    [Fact]
    public void ParseHeader_MissingHeader_ReturnsNull()
    {
        var response = "HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\n\r\n";
        var location = SsdpDiscovery.ParseHeader(response, "Location");
        location.Should().BeNull();
    }

    [Fact]
    public void IsOnSameSubnet_SameSubnet_ReturnsTrue()
    {
        var a = IPAddress.Parse("192.168.1.10");
        var b = IPAddress.Parse("192.168.1.20");
        var mask = IPAddress.Parse("255.255.255.0");
        SsdpDiscovery.IsOnSameSubnet(a, b, mask).Should().BeTrue();
    }

    [Fact]
    public void IsOnSameSubnet_DifferentSubnet_ReturnsFalse()
    {
        var a = IPAddress.Parse("192.168.1.10");
        var b = IPAddress.Parse("192.168.2.20");
        var mask = IPAddress.Parse("255.255.255.0");
        SsdpDiscovery.IsOnSameSubnet(a, b, mask).Should().BeFalse();
    }

    [Fact]
    public void NotifyMessage_ParsesLocation()
    {
        // Verify NOTIFY format is parseable by ParseHeader
        var notify = "NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nNT: upnp:rootdevice\r\nNTS: ssdp:alive\r\nLocation: http://192.168.1.1:5000/rootDesc.xml\r\n\r\n";
        var location = SsdpDiscovery.ParseHeader(notify, "Location");
        location.Should().Be("http://192.168.1.1:5000/rootDesc.xml");

        // Verify first line starts with NOTIFY (the ProcessDatagram check)
        notify.Should().StartWith("NOTIFY");
    }
}
