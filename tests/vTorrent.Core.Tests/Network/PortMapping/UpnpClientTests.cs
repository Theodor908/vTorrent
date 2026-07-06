using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using vTorrent.Core.Network.PortMapping;
using PortMappingEntry = vTorrent.Core.Network.PortMapping.PortMapping;

namespace vTorrent.Core.Tests.Network.PortMapping;

public class UpnpClientTests : IAsyncLifetime
{
    private MockUpnpDevice _mockDevice = null!;
    private UpnpDevice _device = null!;

    public async Task InitializeAsync()
    {
        _mockDevice = new MockUpnpDevice();
        _mockDevice.Start();

        await using var client = new UpnpClient();
        var device = await client.FetchDeviceDescriptionAsync($"{_mockDevice.BaseUrl}/desc.xml", default);
        device.Should().NotBeNull("mock device should be reachable");
        _device = device!;
    }

    public async Task DisposeAsync()
    {
        await _mockDevice.DisposeAsync();
    }

    [Fact]
    public async Task FetchDeviceDescription_ParsesCorrectly()
    {
        await using var client = new UpnpClient();
        var device = await client.FetchDeviceDescriptionAsync($"{_mockDevice.BaseUrl}/desc.xml", default);
        device.Should().NotBeNull();
        device!.ServiceType.Should().Contain("WANIPConnection");
        device.ControlUrl.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AddMappingAsync_Success()
    {
        await using var client = new UpnpClient();
        var mapping = await client.AddMappingAsync(_device, PortMapProtocol.Tcp, 6881, 6881, 3600);
        mapping.Should().NotBeNull();
        mapping!.Protocol.Should().Be(PortMapProtocol.Tcp);
        mapping.Transport.Should().Be(PortMapTransport.Upnp);
        mapping.InternalPort.Should().Be(6881);
    }

    [Fact]
    public async Task AddMappingAsync_PortConflict_RetriesRandomPort()
    {
        _mockDevice.SoapErrorCode = 718;
        await using var client = new UpnpClient();
        var mapping = await client.AddMappingAsync(_device, PortMapProtocol.Tcp, 6881, 6881, 3600);
        mapping.Should().BeNull();
        _mockDevice.SoapRequestCount.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task AddMappingAsync_PermanentOnly_SetsUseLeaseDurationFalse()
    {
        _mockDevice.SoapErrorCode = 725;
        await using var client = new UpnpClient();
        await client.AddMappingAsync(_device, PortMapProtocol.Tcp, 6881, 6881, 3600);
        _device.UseLeaseDuration.Should().BeFalse();
    }

    [Fact]
    public async Task AddMappingAsync_Error501_RetriesAsPortConflict()
    {
        _mockDevice.SoapErrorCode = 501;
        await using var client = new UpnpClient();
        var mapping = await client.AddMappingAsync(_device, PortMapProtocol.Tcp, 6881, 6881, 3600);
        mapping.Should().BeNull();
        _mockDevice.SoapRequestCount.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task AddMappingAsync_Error724_RetriesWithInternalEqExternal()
    {
        _mockDevice.SoapErrorCode = 724;
        await using var client = new UpnpClient();
        var mapping = await client.AddMappingAsync(_device, PortMapProtocol.Tcp, 6881, 7000, 3600);
        mapping.Should().BeNull();
        _mockDevice.SoapRequestCount.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task DeleteMappingAsync_SendsSoap()
    {
        await using var client = new UpnpClient();
        var mapping = await client.AddMappingAsync(_device, PortMapProtocol.Tcp, 6881, 6881, 3600);
        mapping.Should().NotBeNull();
        var soapCountBefore = _mockDevice.SoapRequestCount;
        await client.DeleteMappingAsync(_device, mapping!);
        _mockDevice.SoapRequestCount.Should().BeGreaterThan(soapCountBefore);
    }

    [Fact]
    public async Task GetExternalIpAsync_ParsesResponse()
    {
        _mockDevice.ExternalIp = "198.51.100.42";
        await using var client = new UpnpClient();
        var ip = await client.GetExternalIpAsync(_device);
        ip.Should().NotBeNull();
        ip!.ToString().Should().Be("198.51.100.42");
    }

    [Fact]
    public async Task AddMappingAsync_RespectsMaxMappingsCap()
    {
        await using var client = new UpnpClient();
        var m1 = await client.AddMappingAsync(_device, PortMapProtocol.Tcp, 6881, 6881, 3600);
        var m2 = await client.AddMappingAsync(_device, PortMapProtocol.Udp, 6881, 6881, 3600);
        m1.Should().NotBeNull();
        m2.Should().NotBeNull();
        client.ActiveMappings.Count.Should().Be(2);
    }

    [Fact]
    public async Task AddMappingAsync_WhenClosing_ReturnsNull()
    {
        await using var client = new UpnpClient();
        client.Close();
        var mapping = await client.AddMappingAsync(_device, PortMapProtocol.Tcp, 6881, 6881, 3600);
        mapping.Should().BeNull();
    }

    [Fact]
    public async Task ActiveMappings_ReturnsSnapshot()
    {
        await using var client = new UpnpClient();
        await client.AddMappingAsync(_device, PortMapProtocol.Tcp, 6881, 6881, 3600);
        var snapshot = client.ActiveMappings;
        snapshot.Count.Should().Be(1);
        snapshot.Should().BeOfType<List<PortMappingEntry>>();
    }
}
