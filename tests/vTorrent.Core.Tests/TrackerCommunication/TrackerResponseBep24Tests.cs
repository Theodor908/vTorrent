using System.Net;
using FluentAssertions;
using vTorrent.Core.TrackerCommunication.Models;
using Xunit;

namespace vTorrent.Core.Tests.TrackerCommunication;

public class TrackerResponseBep24Tests
{
    [Fact]
    public void ExternalIp_IPv4_ParsedCorrectly()
    {
        var response = new TrackerResponse();
        var ipBytes = IPAddress.Parse("203.0.113.42").GetAddressBytes();
        ipBytes.Length.Should().Be(4);
        response.ExternalIp = new IPAddress(ipBytes);
        response.ExternalIp.Should().Be(IPAddress.Parse("203.0.113.42"));
    }

    [Fact]
    public void ExternalIp_IPv6_ParsedCorrectly()
    {
        var response = new TrackerResponse();
        var ipBytes = IPAddress.Parse("2001:db8::1").GetAddressBytes();
        ipBytes.Length.Should().Be(16);
        response.ExternalIp = new IPAddress(ipBytes);
        response.ExternalIp.Should().Be(IPAddress.Parse("2001:db8::1"));
    }

    [Fact]
    public void ExternalIp_Null_WhenNotPresent()
    {
        new TrackerResponse().ExternalIp.Should().BeNull();
    }
}
