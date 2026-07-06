using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Transport;
using vTorrent.Core.Tests.PeerCommunication.Support;
using Xunit;

namespace vTorrent.Core.Tests.PeerCommunication.Transport;

public class IncomingConnectionDispatcherTests
{
    [Fact]
    public async Task PlaintextHandshake_RoutesToResolvedPeerManagerByInfoHash()
    {
        var infoHash = new byte[20]; for (int i = 0; i < 20; i++) infoHash[i] = (byte)i;
        var hex = Convert.ToHexString(infoHash);

        string? routedHex = null;
        var resolved = PeerManagerTestFactory.Create(maxConnections: 10);
        Func<string, PeerManager?> resolve = h => { routedHex = h; return h == hex ? resolved : null; };

        var enc = new StaticOptionsMonitor<EncryptionSettings>(
            new EncryptionSettings { InPolicy = EncryptionPolicy.Disabled });
        var connMonitor = new StaticOptionsMonitor<ConnectionSettings>(new ConnectionSettings());
        var listener = new TransportListener(utpManager: null, new PeerSettings(), null, connMonitor);

        await using var dispatcher = new IncomingConnectionDispatcher(
            listener, resolve, req2 => null, enc, NullLoggerFactory.Instance,
            connectedPeerCount: () => 0, maxSessionConnections: () => 200);

        await dispatcher.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        var port = dispatcher.BoundPort;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var hs = new Handshake(infoHash, new byte[20]).ToBytes();
        await client.GetStream().WriteAsync(hs);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (routedHex == null && sw.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(25);

        routedHex.Should().Be(hex);
    }

    [Fact]
    public async Task PlaintextHandshake_UnknownInfoHash_DisconnectsWithoutThrowing()
    {
        var enc = new StaticOptionsMonitor<EncryptionSettings>(
            new EncryptionSettings { InPolicy = EncryptionPolicy.Disabled });
        var connMonitor = new StaticOptionsMonitor<ConnectionSettings>(new ConnectionSettings());
        var listener = new TransportListener(utpManager: null, new PeerSettings(), null, connMonitor);
        await using var dispatcher = new IncomingConnectionDispatcher(
            listener, _ => null, req2 => null, enc, NullLoggerFactory.Instance,
            connectedPeerCount: () => 0, maxSessionConnections: () => 200);

        await dispatcher.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, dispatcher.BoundPort);
        var hs = new Handshake(new byte[20], new byte[20]).ToBytes();
        await client.GetStream().WriteAsync(hs);
        await Task.Delay(200); // dispatcher must not crash its accept loop
    }
}
