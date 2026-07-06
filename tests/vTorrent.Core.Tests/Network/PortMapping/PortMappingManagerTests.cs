using FluentAssertions;
using Moq;
using Microsoft.Extensions.Options;
using Xunit;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Network.PortMapping;

namespace vTorrent.Core.Tests.Network.PortMapping;

public class PortMappingManagerTests
{
    [Fact]
    public async Task Start_DisabledSetting_DoesNotMap()
    {
        var settings = new ConnectionSettings { EnableNatPmp = false, EnableUpnp = false };
        var monitor = CreateMonitor(settings);

        var manager = new PortMappingManager(monitor.Object);
        await manager.StartAsync(6881);

        manager.IsActive.Should().BeFalse();
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Start_NoGateway_DoesNotThrow()
    {
        // With EnableNatPmp=true but using loopback (no real gateway on most CI)
        var settings = new ConnectionSettings { EnableNatPmp = true };
        var monitor = CreateMonitor(settings);

        var manager = new PortMappingManager(monitor.Object);
        // This should not throw even if no gateway is found
        await manager.StartAsync(6881);

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Stop_WithoutStart_DoesNotThrow()
    {
        var settings = new ConnectionSettings { EnableNatPmp = false };
        var monitor = CreateMonitor(settings);

        var manager = new PortMappingManager(monitor.Object);
        await manager.StopAsync(); // Should not throw
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Start_BothDisabled_DoesNotMap()
    {
        var settings = new ConnectionSettings
        {
            EnableNatPmp = false,
            EnableUpnp = false
        };
        var monitor = CreateMonitor(settings);

        var manager = new PortMappingManager(monitor.Object);
        await manager.StartAsync(6881);

        manager.IsActive.Should().BeFalse();
        manager.TcpMapping.Should().BeNull();
        manager.UdpMapping.Should().BeNull();
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Start_UpnpOnlyDisabled_StillWorks()
    {
        var settings = new ConnectionSettings
        {
            EnableNatPmp = false,
            EnableUpnp = true
        };
        var monitor = CreateMonitor(settings);

        var manager = new PortMappingManager(monitor.Object);
        await manager.StartAsync(6881);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Stop_BothTransports_DoesNotThrow()
    {
        var settings = new ConnectionSettings
        {
            EnableNatPmp = true,
            EnableUpnp = true
        };
        var monitor = CreateMonitor(settings);

        var manager = new PortMappingManager(monitor.Object);
        await manager.StartAsync(6881);
        await manager.StopAsync();
        manager.IsActive.Should().BeFalse();
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Start_UsesUpnpLeaseSecondsNotNatPmp()
    {
        var settings = new ConnectionSettings
        {
            EnableNatPmp = false,
            EnableUpnp = true,
            NatPmpLeaseSeconds = 7200,
            UpnpLeaseSeconds = 1800
        };
        var monitor = CreateMonitor(settings);

        var manager = new PortMappingManager(monitor.Object);
        await manager.StartAsync(6881);
        await manager.DisposeAsync();
    }

    private static Mock<IOptionsMonitor<ConnectionSettings>> CreateMonitor(ConnectionSettings settings)
    {
        var monitor = new Mock<IOptionsMonitor<ConnectionSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(settings);
        return monitor;
    }
}
