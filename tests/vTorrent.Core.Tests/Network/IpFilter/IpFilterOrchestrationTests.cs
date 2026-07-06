using FluentAssertions;
using System.Net;
using Xunit;
using vTorrent.Core.Network.IpFilter;
using vTorrent.Core.Session;
using IpFilterClass = vTorrent.Core.Network.IpFilter.IpFilter;

namespace vTorrent.Core.Tests.Network.IpFilter;

public class IpFilterOrchestrationTests
{
    [Fact]
    public void LoadFromSessionState_BlockedRanges()
    {
        var filter = new IpFilterClass();
        var state = new IpFilterState();
        state.BlockedRanges.Add("10.0.0.0/8");
        state.BlockedRanges.Add("172.16.0.0/12");

        IpFilterStartup.LoadFromState(filter, state);

        filter.Access(IPAddress.Parse("10.0.0.1")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("172.16.0.1")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("8.8.8.8")).Should().Be(AccessFlags.Allowed);
    }

    [Fact]
    public void LoadFromSessionState_BannedIps()
    {
        var filter = new IpFilterClass();
        var state = new IpFilterState();
        state.BanIp("1.2.3.4", "spam");
        state.BanIp("5.6.7.8", "abuse");

        IpFilterStartup.LoadFromState(filter, state);

        filter.Access(IPAddress.Parse("1.2.3.4")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("5.6.7.8")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("9.9.9.9")).Should().Be(AccessFlags.Allowed);
    }

    [Fact]
    public void ThreadSafeSwap_NewFilterReplacesOld()
    {
        var holder = new IpFilterHolder();
        var filter1 = new IpFilterClass();
        filter1.AddRule(IPAddress.Parse("1.0.0.0"), IPAddress.Parse("1.0.0.255"), AccessFlags.Blocked);
        holder.Update(filter1);
        holder.Current.Access(IPAddress.Parse("1.0.0.1")).Should().Be(AccessFlags.Blocked);

        var filter2 = new IpFilterClass();
        holder.Update(filter2);
        holder.Current.Access(IPAddress.Parse("1.0.0.1")).Should().Be(AccessFlags.Allowed);
    }
}
