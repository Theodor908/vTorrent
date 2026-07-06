using FluentAssertions;
using Xunit;
using vTorrent.Core.TrackerCommunication.Models;

namespace vTorrent.Core.Tests.TrackerCommunication;

public class PrivateTrackerRequestTests
{
    private static readonly byte[] TestInfoHash = new byte[20];
    private static readonly byte[] TestPeerId = new byte[20];
    private const int TestPort = 6881;

    [Fact]
    public void IsPrivateTorrent_DefaultsFalse()
    {
        var request = TrackerRequest.CreateStarted(TestInfoHash, TestPeerId, TestPort, 1000);
        request.IsPrivateTorrent.Should().BeFalse();
    }

    [Fact]
    public void Ipv4Address_IncludedWhenSet()
    {
        var request = TrackerRequest.CreateStarted(TestInfoHash, TestPeerId, TestPort, 1000);
        request.IsPrivateTorrent = true;
        request.Ipv4Address = "203.0.113.42";

        request.Ipv4Address.Should().Be("203.0.113.42");
        request.IsPrivateTorrent.Should().BeTrue();
    }

    [Fact]
    public void Ipv6Address_IncludedWhenSet()
    {
        var request = TrackerRequest.CreateStarted(TestInfoHash, TestPeerId, TestPort, 1000);
        request.IsPrivateTorrent = true;
        request.Ipv6Address = "2001:db8::1";

        request.Ipv6Address.Should().Be("2001:db8::1");
    }

    [Fact]
    public void WithUpdatedStats_PreservesPrivateFields()
    {
        var request = TrackerRequest.CreateRegular(TestInfoHash, TestPeerId, TestPort, 100, 200, 300);
        request.IsPrivateTorrent = true;
        request.Ipv4Address = "203.0.113.42";
        request.Ipv6Address = "2001:db8::1";

        var updated = request.WithUpdatedStats(500, 600, 700);

        updated.IsPrivateTorrent.Should().BeTrue();
        updated.Ipv4Address.Should().Be("203.0.113.42");
        updated.Ipv6Address.Should().Be("2001:db8::1");
    }
}
