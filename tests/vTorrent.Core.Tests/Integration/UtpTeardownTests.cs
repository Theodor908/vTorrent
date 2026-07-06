using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using vTorrent.Abstractions.Enums;
using vTorrent.Core.Network;
using vTorrent.Core.PeerCommunication.Transport.Utp;
using Xunit;

namespace vTorrent.Tests.Integration;

/// <summary>
/// TODO 3 (UTP_COMPLETION_HANDOFF): graceful teardown via ST_FIN. Closing one end must
/// send a FIN so the peer's reader observes EOF after draining all data, both sockets end
/// up Closed, and neither leaks in its UtpSocketManager registration table.
/// </summary>
public class UtpTeardownTests
{
    private static async Task<(UtpSocketManager u1, UtpSocketManager u2, UtpSocket a, UtpSocket b,
        UdpSocketManager d1, UdpSocketManager d2)>
        ConnectPairAsync(CancellationToken ct)
    {
        var udp1 = new UdpSocketManager();
        var udp2 = new UdpSocketManager();
        await udp1.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);
        await udp2.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);
        var utp1 = new UtpSocketManager((data, ep) => udp1.SendAsync(data, ep, UdpSendFlags.PeerConnection));
        var utp2 = new UtpSocketManager((data, ep) => udp2.SendAsync(data, ep, UdpSendFlags.PeerConnection));
        udp1.SetUtpHandler(utp1);
        udp2.SetUtpHandler(utp2);
        var connectTask = utp1.ConnectAsync(new IPEndPoint(IPAddress.Loopback, udp2.LocalPort), ct);
        var accepted = await utp2.AcceptAsync(ct);
        var connected = await connectTask.WaitAsync(ct);
        return (utp1, utp2, connected, accepted, udp1, udp2);
    }

    private static async Task PollUntilAsync(Func<bool> cond, CancellationToken ct, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed > TimeSpan.FromSeconds(8))
                throw new TimeoutException($"condition not met: {what}");
            await Task.Delay(20, ct);
        }
    }

    [Fact]
    public async Task GracefulClose_SendsFin_ReaderGetsEof_SocketsUnregister()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (u1, u2, a, b, d1, d2) = await ConnectPairAsync(cts.Token);
        using var _ = u1; using var __ = u2; using var ___ = d1; using var ____ = d2;
        var A = new UtpTransportStream(a);
        using var B = new UtpTransportStream(b);

        var data = new byte[3000]; // 3 packets
        Random.Shared.NextBytes(data);
        await A.WriteAsync(data, cts.Token);

        // Close the writer gracefully — this sends ST_FIN.
        A.Dispose();

        // The reader must receive every byte, then EOF (ReadAsync returns 0).
        var received = new byte[data.Length];
        int total = 0;
        while (true)
        {
            int n = await B.ReadAsync(received.AsMemory(total), cts.Token);
            if (n == 0) break; // EOF
            total += n;
            if (total > data.Length) throw new InvalidOperationException("read more than sent");
        }

        total.Should().Be(data.Length, "all data must be delivered before EOF");
        received.Should().Equal(data, "graceful close must not truncate buffered data");

        // The accepting side observes the close.
        await PollUntilAsync(() => b.State == UtpConnectionState.Closed, cts.Token, "b Closed");
        a.State.Should().Be(UtpConnectionState.Closed);

        // Neither socket leaks in its manager (the tick sweep unregisters Closed sockets).
        await PollUntilAsync(() => u1.RegisteredSocketCount == 0, cts.Token, "u1 empty");
        await PollUntilAsync(() => u2.RegisteredSocketCount == 0, cts.Token, "u2 empty");
    }
}
