using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace vTorrent.Core.Network.PortMapping;

public static class GatewayDiscovery
{
    public static IPAddress? DiscoverGateway(IPAddress? listenAddress = null)
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Where(ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var ni in interfaces)
            {
                var props = ni.GetIPProperties();

                if (listenAddress != null && !IPAddress.Any.Equals(listenAddress))
                {
                    var hasAddress = props.UnicastAddresses
                        .Any(ua => ua.Address.Equals(listenAddress));
                    if (!hasAddress) continue;
                }

                var gateway = props.GatewayAddresses
                    .FirstOrDefault(ga => ga.Address.AddressFamily == AddressFamily.InterNetwork
                                       && !IPAddress.None.Equals(ga.Address)
                                       && !IPAddress.Any.Equals(ga.Address));

                if (gateway != null)
                    return gateway.Address;
            }
        }
        catch { }

        return null;
    }

    /// <summary>Resolve the subnet mask for a given local IP address from the OS network interfaces.</summary>
    public static IPAddress? GetSubnetMask(IPAddress localAddress)
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up);

            foreach (var ni in interfaces)
            {
                var props = ni.GetIPProperties();
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.Equals(localAddress) && ua.IPv4Mask != null)
                        return ua.IPv4Mask;
                }
            }
        }
        catch { }
        return null;
    }
}
