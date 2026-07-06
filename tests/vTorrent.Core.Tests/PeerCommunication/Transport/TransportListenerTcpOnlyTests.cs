using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Transport;
using Xunit;

namespace vTorrent.Core.Tests.PeerCommunication.Transport;

public class TransportListenerTcpOnlyTests
{
    [Fact]
    public async Task AcceptsTcp_WhenUtpManagerIsNull()
    {
        var settings = new PeerSettings();
        var connMonitor = new StaticOptionsMonitor<ConnectionSettings>(new ConnectionSettings());
        await using var listener = new TransportListener(utpManager: null, settings, logger: null, connectionMonitor: connMonitor);

        await listener.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        var boundPort = listener.BoundPort;
        boundPort.Should().BeGreaterThan(0);

        using var client = new TcpClient();
        var acceptTask = listener.AcceptAsync();
        await client.ConnectAsync(IPAddress.Loopback, boundPort);

        var stream = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        stream.Should().NotBeNull();
    }
}

// Minimal test double for IOptionsMonitor<T>.
public sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public System.IDisposable? OnChange(System.Action<T, string?> listener) => null;
}
