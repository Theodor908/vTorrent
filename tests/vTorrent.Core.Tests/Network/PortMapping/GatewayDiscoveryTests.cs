using FluentAssertions;
using System.Net;
using Xunit;
using vTorrent.Core.Network.PortMapping;

namespace vTorrent.Core.Tests.Network.PortMapping;

public class GatewayDiscoveryTests
{
    [Fact]
    public void DiscoverGateway_DoesNotThrow()
    {
        var gateway = GatewayDiscovery.DiscoverGateway();
        // Can't assert non-null (CI may not have a gateway)
    }

    [Fact]
    public void DiscoverGateway_WithLoopback_DoesNotThrow()
    {
        var gateway = GatewayDiscovery.DiscoverGateway(IPAddress.Loopback);
        // Loopback typically has no gateway
    }

    [Fact]
    public void DiscoverGateway_ReturnsIPv4IfFound()
    {
        var gateway = GatewayDiscovery.DiscoverGateway();
        if (gateway != null)
            gateway.AddressFamily.Should().Be(System.Net.Sockets.AddressFamily.InterNetwork);
    }
}
