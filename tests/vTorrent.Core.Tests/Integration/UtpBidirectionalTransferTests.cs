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

/// <summary>
/// Permanent regression coverage for bidirectional / multi-packet uTP transfer.
/// Locks in the SYN-ACK sequence off-by-one fix in <see cref="UtpSocket.ProcessState"/>
/// (docs/UTP_STACK_FINDINGS.md): before the fix, responder->initiator data packets were
/// received but never delivered, so any reverse-direction or ping-pong flow hung.
/// (Promoted from the original "UtpBidirectionalDiagnosisTests" scratch harness.)
/// </summary>
public class UtpBidirectionalTransferTests
{
    private static async Task<(UtpSocketManager u1, UtpSocketManager u2, UtpSocket a, UtpSocket b, UdpSocketManager d1, UdpSocketManager d2)>
        ConnectPairAsync()
    {
        var udp1 = new UdpSocketManager();
        var udp2 = new UdpSocketManager();
        await udp1.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);
        await udp2.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);
        var utp1 = new UtpSocketManager((data, ep) => udp1.SendAsync(data, ep, UdpSendFlags.PeerConnection));
        var utp2 = new UtpSocketManager((data, ep) => udp2.SendAsync(data, ep, UdpSendFlags.PeerConnection));
        udp1.SetUtpHandler(utp1);
        udp2.SetUtpHandler(utp2);
        var connectTask = utp1.ConnectAsync(new IPEndPoint(IPAddress.Loopback, udp2.LocalPort), CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = await utp2.AcceptAsync(cts.Token);
        var connected = await connectTask.WaitAsync(TimeSpan.FromSeconds(5));
        return (utp1, utp2, connected, accepted, udp1, udp2);
    }

    private static async Task ReadExactAsync(UtpTransportStream s, byte[] buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(total), ct);
            if (n == 0) throw new InvalidOperationException("unexpected EOF during read");
            total += n;
        }
    }

    // Reverse direction only (B -> A): the accepted side's send path must deliver.
    [Fact]
    public async Task ReverseDirection_ResponderToInitiator_Delivers()
    {
        var (u1, u2, a, b, d1, d2) = await ConnectPairAsync();
        using var _ = u1; using var __ = u2; using var ___ = d1; using var ____ = d2;
        using var A = new UtpTransportStream(a);
        using var B = new UtpTransportStream(b);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var data = new byte[124];
        Random.Shared.NextBytes(data);
        await B.WriteAsync(data, cts.Token);
        var got = new byte[124];
        await ReadExactAsync(A, got, cts.Token);
        got.Should().Equal(data);
    }

    // Ping-pong request/response (A->B then B->A), mirrors the MSE crypto phase shape.
    [Fact]
    public async Task PingPong_RequestResponse_Completes()
    {
        var (u1, u2, a, b, d1, d2) = await ConnectPairAsync();
        using var _ = u1; using var __ = u2; using var ___ = d1; using var ____ = d2;
        using var A = new UtpTransportStream(a);
        using var B = new UtpTransportStream(b);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        // Responder: read 124 (in 128-chunks like the MSE scan), then write 14 back.
        var responder = Task.Run(async () =>
        {
            var chunk = new byte[128];
            int total = 0;
            while (total < 124)
            {
                int n = await B.ReadAsync(chunk.AsMemory(0, 128), cts.Token);
                if (n == 0) throw new InvalidOperationException("EOF");
                total += n;
            }
            await B.WriteAsync(new byte[14], cts.Token);
        });

        // Initiator: write 40 then 84 (crypto_provide as 2 writes), then read 14 back.
        await A.WriteAsync(new byte[40], cts.Token);
        await A.WriteAsync(new byte[84], cts.Token);
        var reply = new byte[14];
        await ReadExactAsync(A, reply, cts.Token);
        await responder;
    }

    // Several interleaved rounds (both directions carry data repeatedly).
    [Fact]
    public async Task MultiRound_Interleaved_Completes()
    {
        var (u1, u2, a, b, d1, d2) = await ConnectPairAsync();
        using var _ = u1; using var __ = u2; using var ___ = d1; using var ____ = d2;
        using var A = new UtpTransportStream(a);
        using var B = new UtpTransportStream(b);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var responder = Task.Run(async () =>
        {
            for (int round = 0; round < 5; round++)
            {
                var buf = new byte[100];
                await ReadExactAsync(B, buf, cts.Token);
                await B.WriteAsync(new byte[50], cts.Token);
            }
        });

        for (int round = 0; round < 5; round++)
        {
            await A.WriteAsync(new byte[100], cts.Token);
            var buf = new byte[50];
            await ReadExactAsync(A, buf, cts.Token);
        }
        await responder;
    }
}
