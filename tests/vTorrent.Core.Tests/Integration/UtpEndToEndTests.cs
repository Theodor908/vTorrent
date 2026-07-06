using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using vTorrent.Abstractions.Enums;
using vTorrent.Core.Network;
using vTorrent.Core.PeerCommunication.Transport.Utp;
using Xunit;

namespace vTorrent.Tests.Integration;

public class UtpEndToEndTests
{
    [Fact]
    public async Task TwoManagers_Loopback_TransferData()
    {
        // Setup: two UdpSocketManagers on loopback
        using var udp1 = new UdpSocketManager();
        using var udp2 = new UdpSocketManager();
        await udp1.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);
        await udp2.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);

        var utp1 = new UtpSocketManager((data, ep) => udp1.SendAsync(data, ep, UdpSendFlags.PeerConnection));
        var utp2 = new UtpSocketManager((data, ep) => udp2.SendAsync(data, ep, UdpSendFlags.PeerConnection));
        udp1.SetUtpHandler(utp1);
        udp2.SetUtpHandler(utp2);

        // Connect from manager1 to manager2
        var connectTask = utp1.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, udp2.LocalPort),
            CancellationToken.None);

        // Accept on manager2
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = await utp2.AcceptAsync(cts.Token);
        var connected = await connectTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Transfer data
        using var outgoing = new UtpTransportStream(connected);
        using var incoming = new UtpTransportStream(accepted);

        var testData = new byte[10_000];
        Random.Shared.NextBytes(testData);
        await outgoing.WriteAsync(testData);

        var received = new byte[10_000];
        int totalRead = 0;
        while (totalRead < 10_000)
        {
            totalRead += await incoming.ReadAsync(
                received.AsMemory(totalRead), cts.Token);
        }

        received.Should().BeEquivalentTo(testData);

        // Cleanup
        utp1.Dispose();
        utp2.Dispose();
    }
}
