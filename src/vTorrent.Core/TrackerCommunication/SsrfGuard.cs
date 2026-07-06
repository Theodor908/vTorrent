using System.Net;

namespace vTorrent.Core.TrackerCommunication;

/// <summary>
/// Guards against Server-Side Request Forgery (SSRF) by rejecting
/// private / loopback / link-local IP addresses in tracker URLs.
/// </summary>
internal static class SsrfGuard
{
    /// <summary>
    /// Returns true when the given IP address belongs to a private, loopback,
    /// or link-local range and should therefore be blocked when SSRF mitigation
    /// is enabled.
    /// </summary>
    public static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return true;

        // Map IPv6-mapped-IPv4 to plain IPv4 for range checks
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();

            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;

            // 172.16.0.0/12  (172.16.x.x – 172.31.x.x)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;

            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the hostname from a tracker URL and checks whether any of
    /// the resolved addresses are private.  Returns true when the request
    /// should be blocked.
    /// </summary>
    public static bool ShouldBlock(string host)
    {
        // If the host is already an IP literal, check directly
        if (IPAddress.TryParse(host, out var literal))
            return IsPrivateAddress(literal);

        try
        {
            var addresses = Dns.GetHostAddresses(host);
            foreach (var addr in addresses)
            {
                if (IsPrivateAddress(addr))
                    return true;
            }
        }
        catch
        {
            // DNS failure — let the caller handle the connection error naturally
            return false;
        }

        return false;
    }
}
