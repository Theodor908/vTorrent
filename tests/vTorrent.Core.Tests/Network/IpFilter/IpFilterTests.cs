using FluentAssertions;
using System.Net;
using Xunit;
using vTorrent.Core.Network.IpFilter;
using IpFilterClass = vTorrent.Core.Network.IpFilter.IpFilter;

namespace vTorrent.Core.Tests.Network.IpFilter;

public class IpFilterTests
{
    [Fact]
    public void Default_AllowsEverything()
    {
        var filter = new IpFilterClass();
        filter.Access(IPAddress.Parse("1.2.3.4")).Should().Be(AccessFlags.Allowed);
        filter.Access(IPAddress.Parse("255.255.255.255")).Should().Be(AccessFlags.Allowed);
    }

    [Fact]
    public void AddBlockedRange_BlocksIpsInRange()
    {
        var filter = new IpFilterClass();
        filter.AddRule(IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.0.0.255"), AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("10.0.0.0")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("10.0.0.128")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("10.0.0.255")).Should().Be(AccessFlags.Blocked);
    }

    [Fact]
    public void AddBlockedRange_AllowsIpsOutsideRange()
    {
        var filter = new IpFilterClass();
        filter.AddRule(IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.0.0.255"), AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("9.255.255.255")).Should().Be(AccessFlags.Allowed);
        filter.Access(IPAddress.Parse("10.0.1.0")).Should().Be(AccessFlags.Allowed);
        filter.Access(IPAddress.Parse("192.168.1.1")).Should().Be(AccessFlags.Allowed);
    }

    [Fact]
    public void OverlappingRules_LastWins()
    {
        var filter = new IpFilterClass();
        filter.AddRule(IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.0.0.255"), AccessFlags.Blocked);
        filter.AddRule(IPAddress.Parse("10.0.0.100"), IPAddress.Parse("10.0.0.200"), AccessFlags.Allowed);
        filter.Access(IPAddress.Parse("10.0.0.50")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("10.0.0.150")).Should().Be(AccessFlags.Allowed);
        filter.Access(IPAddress.Parse("10.0.0.250")).Should().Be(AccessFlags.Blocked);
    }

    [Fact]
    public void AdjacentRanges_MergedCorrectly()
    {
        var filter = new IpFilterClass();
        filter.AddRule(IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.0.0.100"), AccessFlags.Blocked);
        filter.AddRule(IPAddress.Parse("10.0.0.101"), IPAddress.Parse("10.0.0.200"), AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("10.0.0.50")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("10.0.0.150")).Should().Be(AccessFlags.Blocked);
    }

    [Fact]
    public void SingleIp_BlocksExactly()
    {
        var filter = new IpFilterClass();
        filter.AddRule(IPAddress.Parse("1.2.3.4"), IPAddress.Parse("1.2.3.4"), AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("1.2.3.3")).Should().Be(AccessFlags.Allowed);
        filter.Access(IPAddress.Parse("1.2.3.4")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("1.2.3.5")).Should().Be(AccessFlags.Allowed);
    }

    [Fact]
    public void AddRuleFromCidr_ParsesAndBlocks()
    {
        var filter = new IpFilterClass();
        filter.AddRuleFromCidr("192.168.1.0/24", AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("192.168.1.0")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("192.168.1.255")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("192.168.2.0")).Should().Be(AccessFlags.Allowed);
    }

    [Fact]
    public void LargeBlocklist_LookupPerformance()
    {
        var filter = new IpFilterClass();
        for (int i = 0; i < 100_000; i++)
        {
            byte b1 = (byte)(i >> 8);
            byte b2 = (byte)(i & 0xFF);
            filter.AddRule(
                IPAddress.Parse($"{b1}.{b2}.0.0"),
                IPAddress.Parse($"{b1}.{b2}.0.255"),
                AccessFlags.Blocked);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
            filter.Access(IPAddress.Parse("128.128.0.128"));
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(100);
    }
}
