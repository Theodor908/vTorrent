using System.Net;
using FluentAssertions;
using vTorrent.Core.Network;
using Xunit;

namespace vTorrent.Core.Tests.Network;

public class InterfaceResolverTests
{
    [Fact]
    public void Resolve_IpAddressString_ReturnsIPAddress()
    {
        var result = InterfaceResolver.Resolve("192.168.1.1");
        result.Should().Be(IPAddress.Parse("192.168.1.1"));
    }

    [Fact]
    public void Resolve_AnyAddress_ReturnsNull()
    {
        InterfaceResolver.Resolve("0.0.0.0").Should().BeNull();
    }

    [Fact]
    public void Resolve_EmptyString_ReturnsNull()
    {
        InterfaceResolver.Resolve("").Should().BeNull();
        InterfaceResolver.Resolve(null!).Should().BeNull();
    }

    [Fact]
    public void Resolve_UnknownInterfaceName_ReturnsNull()
    {
        InterfaceResolver.Resolve("nonexistent_iface_xyz").Should().BeNull();
    }

    [Fact]
    public void GetAvailableInterfaces_ReturnsNonEmpty()
    {
        var interfaces = InterfaceResolver.GetAvailableInterfaces();
        interfaces.Should().NotBeNull();
    }

    [Fact]
    public void IsInterfaceUp_UnknownInterface_ReturnsFalse()
    {
        InterfaceResolver.IsInterfaceUp("nonexistent_iface_xyz").Should().BeFalse();
    }

    [Fact]
    public void IsInterfaceUp_EmptyString_ReturnsFalse()
    {
        InterfaceResolver.IsInterfaceUp("").Should().BeFalse();
    }
}
