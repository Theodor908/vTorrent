using System.Net;
using vTorrent.Core.Session;

namespace vTorrent.Core.Network.IpFilter;

public static class IpFilterStartup
{
    public static void LoadFromState(IpFilter filter, IpFilterState state)
    {
        foreach (var cidr in state.BlockedRanges)
            filter.AddRuleFromCidr(cidr, AccessFlags.Blocked);

        foreach (var cidr in state.AllowedRanges)
            filter.AddRuleFromCidr(cidr, AccessFlags.Allowed);

        foreach (var banned in state.BannedIps)
        {
            if (IPAddress.TryParse(banned.Ip, out var addr))
                filter.AddRule(addr, addr, AccessFlags.Blocked);
        }
    }
}
