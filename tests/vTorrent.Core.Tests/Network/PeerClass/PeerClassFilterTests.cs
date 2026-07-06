using FluentAssertions;
using System.Net;
using Xunit;
using vTorrent.Core.Network.PeerClass;

namespace vTorrent.Core.Tests.Network.PeerClass;

public class PeerClassFilterTests
{
    [Fact]
    public void Default_ReturnsClassZero()
    {
        var filter = new PeerClassFilter();
        filter.Classify(IPAddress.Parse("1.2.3.4")).Should().Be(0);
        filter.Classify(IPAddress.Parse("192.168.1.1")).Should().Be(0);
    }

    [Fact]
    public void AddRange_ClassifiesCorrectly()
    {
        var filter = new PeerClassFilter();
        filter.AddRule(IPAddress.Parse("192.168.0.0"), IPAddress.Parse("192.168.255.255"), 1);
        filter.Classify(IPAddress.Parse("192.168.1.100")).Should().Be(1);
        filter.Classify(IPAddress.Parse("10.0.0.1")).Should().Be(0);
    }

    [Fact]
    public void AddCidr_ClassifiesCorrectly()
    {
        var filter = new PeerClassFilter();
        filter.AddRuleFromCidr("10.0.0.0/8", 2);
        filter.Classify(IPAddress.Parse("10.0.0.1")).Should().Be(2);
        filter.Classify(IPAddress.Parse("10.255.255.255")).Should().Be(2);
        filter.Classify(IPAddress.Parse("11.0.0.0")).Should().Be(0);
    }

    [Fact]
    public void MultipleClasses_CorrectMapping()
    {
        var filter = new PeerClassFilter();
        filter.AddRuleFromCidr("192.168.0.0/16", 1);
        filter.AddRuleFromCidr("10.0.0.0/8", 2);
        filter.Classify(IPAddress.Parse("192.168.1.1")).Should().Be(1);
        filter.Classify(IPAddress.Parse("10.0.0.50")).Should().Be(2);
        filter.Classify(IPAddress.Parse("8.8.8.8")).Should().Be(0);
    }

    [Fact]
    public void OverlappingRanges_LastWins()
    {
        var filter = new PeerClassFilter();
        filter.AddRuleFromCidr("10.0.0.0/8", 1);
        filter.AddRuleFromCidr("10.0.0.0/24", 2);
        filter.Classify(IPAddress.Parse("10.0.0.50")).Should().Be(2);
        filter.Classify(IPAddress.Parse("10.0.1.50")).Should().Be(1);
    }
}
