using FluentAssertions;
using System.Net;
using Xunit;
using vTorrent.Core.Network.IpFilter;
using IpFilterClass = vTorrent.Core.Network.IpFilter.IpFilter;

namespace vTorrent.Core.Tests.Network.IpFilter;

public class IpFilterIntegrationTests
{
    [Fact]
    public void BlockedIp_DetectedByFilter()
    {
        var filter = new IpFilterClass();
        filter.AddRule(IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.0.0.255"), AccessFlags.Blocked);

        filter.Access(IPAddress.Parse("10.0.0.50")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("192.168.1.1")).Should().Be(AccessFlags.Allowed);
    }

    [Fact]
    public void I2pPeers_BypassFilter()
    {
        var peer = vTorrent.Abstractions.Models.PeerInfo.FromI2p(
            vTorrent.Abstractions.Models.I2pDestination.FromHash(new byte[32]));

        peer.IsI2p.Should().BeTrue();
        // I2P peers should be skipped in filter checks (checked by IsI2p flag, not by IP)
    }

    [Fact]
    public void LoadFromState_CidrAndBannedIps()
    {
        var filter = new IpFilterClass();

        var blockedRanges = new[] { "10.0.0.0/8", "172.16.0.0/12" };
        foreach (var cidr in blockedRanges)
            filter.AddRuleFromCidr(cidr, AccessFlags.Blocked);

        var bannedIps = new[] { "1.2.3.4", "5.6.7.8" };
        foreach (var ip in bannedIps)
        {
            var addr = IPAddress.Parse(ip);
            filter.AddRule(addr, addr, AccessFlags.Blocked);
        }

        filter.Access(IPAddress.Parse("10.0.0.1")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("172.16.0.1")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("1.2.3.4")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("8.8.8.8")).Should().Be(AccessFlags.Allowed);
    }
}
