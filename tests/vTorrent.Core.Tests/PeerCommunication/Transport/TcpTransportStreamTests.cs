using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Transport;
using vTorrent.Core.PeerCommunication.Transport.Tcp;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication.Transport;

public class TcpTransportStreamTests
{
    [Fact]
    public void TransportType_ShouldBeTcp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        using var accepted = listener.AcceptTcpClient();

        using var stream = new TcpTransportStream(accepted, new PeerSettings());

        stream.TransportType.Should().Be(TransportType.Tcp);
        stream.IsConnected.Should().BeTrue();
        stream.RemoteEndPoint.Should().NotBeNull();

        listener.Stop();
    }

    [Fact]
    public async Task ReadAsync_WriteAsync_RoundTrip()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var server = await listener.AcceptTcpClientAsync();

        using var clientStream = new TcpTransportStream(client, new PeerSettings());
        using var serverStream = new TcpTransportStream(server, new PeerSettings());

        var data = new byte[] { 1, 2, 3, 4, 5 };
        await clientStream.WriteAsync(data);

        var buffer = new byte[10];
        var read = await serverStream.ReadAsync(buffer);

        read.Should().Be(5);
        buffer[..5].Should().BeEquivalentTo(data);

        listener.Stop();
    }
}
