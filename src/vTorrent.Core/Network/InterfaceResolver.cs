using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Network;

/// <summary>
/// Resolves network interface names to IP addresses.
/// Pure utility — no state, no dependencies.
/// </summary>
public static class InterfaceResolver
{
    public static IPAddress? Resolve(string interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
            return null;

        if (IPAddress.TryParse(interfaceName, out var parsed))
        {
            if (parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any))
                return null;
            return parsed;
        }

        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (string.Equals(iface.Name, interfaceName, System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(iface.Description, interfaceName, System.StringComparison.OrdinalIgnoreCase))
                {
                    var ipProps = iface.GetIPProperties();
                    var addr = ipProps.UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    return addr?.Address;
                }
            }
        }
        catch { }

        return null;
    }

    public static List<NetworkInterfaceInfo> GetAvailableInterfaces()
    {
        var result = new List<NetworkInterfaceInfo>();

        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = iface.GetIPProperties();
                var ipv4 = ipProps.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

                result.Add(new NetworkInterfaceInfo
                {
                    Name = iface.Name,
                    Description = iface.Description,
                    IpAddress = ipv4?.Address.ToString() ?? "",
                    IsUp = iface.OperationalStatus == OperationalStatus.Up
                });
            }
        }
        catch { }

        return result;
    }

    public static bool IsInterfaceUp(string interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
            return false;

        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (string.Equals(iface.Name, interfaceName, System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(iface.Description, interfaceName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return iface.OperationalStatus == OperationalStatus.Up;
                }
            }
        }
        catch { }

        return false;
    }
}
