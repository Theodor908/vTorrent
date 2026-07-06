using FluentAssertions;
using System.Net;
using Xunit;
using vTorrent.Core.Network.PortMapping;

namespace vTorrent.Core.Tests.Network.PortMapping;

public class NatPmpClientTests : IAsyncLifetime
{
    private MockNatPmpGateway _gw = null!;

    public async Task InitializeAsync()
    {
        _gw = new MockNatPmpGateway();
        _gw.Start();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _gw.DisposeAsync();

    [Fact]
    public async Task PcpRequest_SendsVersion2()
    {
        var client = new NatPmpClient(IPAddress.Loopback, _gw.Port);
        var mapping = await client.AddMappingAsync(PortMapProtocol.Tcp, 6881, 6881);

        mapping.Should().NotBeNull();
        mapping!.ExternalPort.Should().Be(_gw.ResponseExternalPort);
        _gw.RequestCount.Should().BeGreaterOrEqualTo(1);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task PcpUnsupported_FallsBackToNatPmp()
    {
        _gw.RespondWithPcpUnsupported = true;

        var client = new NatPmpClient(IPAddress.Loopback, _gw.Port);
        var mapping = await client.AddMappingAsync(PortMapProtocol.Tcp, 6881, 6881);

        mapping.Should().NotBeNull();
        _gw.RequestCount.Should().BeGreaterOrEqualTo(2); // PCP attempt + NAT-PMP attempt

        await client.DisposeAsync();
    }

    [Fact]
    public async Task MapTcp_ReturnsExternalPort()
    {
        _gw.ResponseExternalPort = 54321;
        _gw.RespondWithPcpUnsupported = true; // use NAT-PMP for simplicity

        var client = new NatPmpClient(IPAddress.Loopback, _gw.Port);
        var mapping = await client.AddMappingAsync(PortMapProtocol.Tcp, 6881, 6881);

        mapping.Should().NotBeNull();
        mapping!.ExternalPort.Should().Be(54321);
        mapping.Protocol.Should().Be(PortMapProtocol.Tcp);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task MapUdp_ReturnsExternalPort()
    {
        _gw.ResponseExternalPort = 54322;
        _gw.RespondWithPcpUnsupported = true;

        var client = new NatPmpClient(IPAddress.Loopback, _gw.Port);
        var mapping = await client.AddMappingAsync(PortMapProtocol.Udp, 6881, 6881);

        mapping.Should().NotBeNull();
        mapping!.ExternalPort.Should().Be(54322);
        mapping.Protocol.Should().Be(PortMapProtocol.Udp);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task DeleteMapping_SendsLifetimeZero()
    {
        _gw.RespondWithPcpUnsupported = true;
        _gw.ResponseLifetime = 0;

        var client = new NatPmpClient(IPAddress.Loopback, _gw.Port);
        var mapping = await client.AddMappingAsync(PortMapProtocol.Tcp, 6881, 6881);
        mapping.Should().NotBeNull();

        var deleted = await client.DeleteMappingAsync(mapping!);
        deleted.Should().BeTrue();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Timeout_RetriesMultipleTimes()
    {
        _gw.Silent = true; // No responses — forces timeout

        var client = new NatPmpClient(IPAddress.Loopback, _gw.Port, maxRetries: 3, baseRetryMs: 50);
        var mapping = await client.AddMappingAsync(PortMapProtocol.Tcp, 6881, 6881);

        mapping.Should().BeNull(); // All retries exhausted
        _gw.RequestCount.Should().BeGreaterOrEqualTo(3);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ExternalIp_ExtractedFromResponse()
    {
        _gw.ResponseExternalIp = new byte[] { 198, 51, 100, 42 };
        _gw.RespondWithPcpUnsupported = true;

        var client = new NatPmpClient(IPAddress.Loopback, _gw.Port);

        // Get public address
        var externalIp = await client.GetExternalAddressAsync();
        externalIp.Should().NotBeNull();
        externalIp!.ToString().Should().Be("198.51.100.42");

        await client.DisposeAsync();
    }
}
