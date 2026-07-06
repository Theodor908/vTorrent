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
/// TODO 1 (UTP_COMPLETION_HANDOFF): retransmission recovery over a lossy path.
/// A send-callback shim drops the first transmission of a chosen data packet on the
/// initiator->responder path; the transfer must still complete because
/// <see cref="UtpSocket.Tick"/> retransmits the timed-out packet. Without the
/// retransmission logic these tests hang until the CancellationToken fires and fail.
/// </summary>
public class UtpLossRecoveryTests
{
    /// <summary>Wraps a send callback so the Nth *data* packet's first transmission is dropped once.</summary>
    private sealed class DropNthDataPacket
    {
        private readonly Func<ReadOnlyMemory<byte>, IPEndPoint, ValueTask> _inner;
        private readonly int _target;
        private int _dataSeen;
        public int DroppedSeq { get; private set; } = -1;

        public DropNthDataPacket(Func<ReadOnlyMemory<byte>, IPEndPoint, ValueTask> inner, int targetDataPacket)
        {
            _inner = inner;
            _target = targetDataPacket;
        }

        public ValueTask Send(ReadOnlyMemory<byte> data, IPEndPoint ep)
        {
            var span = data.Span;
            bool isData = span.Length >= 20 && (span[0] >> 4) == (int)UtpPacketType.Data;
            if (isData)
            {
                int n = Interlocked.Increment(ref _dataSeen);
                if (n == _target)
                {
                    // Record the dropped seq (bytes 16-17, big-endian) and swallow the packet.
                    DroppedSeq = (span[16] << 8) | span[17];
                    return ValueTask.CompletedTask;
                }
            }
            return _inner(data, ep);
        }
    }

    private static async Task<(UtpSocketManager u1, UtpSocketManager u2, UtpSocket a, UtpSocket b,
        UdpSocketManager d1, UdpSocketManager d2, DropNthDataPacket loss)>
        ConnectPairWithLossAsync(int dropNthDataPacket, CancellationToken ct)
    {
        var udp1 = new UdpSocketManager();
        var udp2 = new UdpSocketManager();
        await udp1.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);
        await udp2.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);

        var loss = new DropNthDataPacket(
            (data, ep) => udp1.SendAsync(data, ep, UdpSendFlags.PeerConnection), dropNthDataPacket);

        var utp1 = new UtpSocketManager(loss.Send);
        var utp2 = new UtpSocketManager((data, ep) => udp2.SendAsync(data, ep, UdpSendFlags.PeerConnection));
        udp1.SetUtpHandler(utp1);
        udp2.SetUtpHandler(utp2);

        var connectTask = utp1.ConnectAsync(new IPEndPoint(IPAddress.Loopback, udp2.LocalPort), ct);
        var accepted = await utp2.AcceptAsync(ct);
        var connected = await connectTask.WaitAsync(ct);
        return (utp1, utp2, connected, accepted, udp1, udp2, loss);
    }

    private static async Task ReadExactAsync(UtpTransportStream s, byte[] buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(total), ct);
            if (n == 0) throw new InvalidOperationException("unexpected EOF");
            total += n;
        }
    }

    [Theory]
    [InlineData(1)] // first data packet dropped (no RTT estimate yet)
    [InlineData(2)] // middle packet dropped -> out-of-order gap the receiver must hold
    public async Task DroppedDataPacket_IsRetransmitted_AndTransferCompletes(int dropNth)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (u1, u2, a, b, d1, d2, loss) = await ConnectPairWithLossAsync(dropNth, cts.Token);
        using var _ = u1; using var __ = u2; using var ___ = d1; using var ____ = d2;
        using var A = new UtpTransportStream(a);
        using var B = new UtpTransportStream(b);

        // 5000 bytes segments into 4 packets (1400*3 + 800), so a mid-stream drop leaves a
        // real reassembly gap the receiver must hold until the retransmit fills it.
        var payload = new byte[5000];
        Random.Shared.NextBytes(payload);

        var received = new byte[payload.Length];
        var reader = ReadExactAsync(B, received, cts.Token);

        await A.WriteAsync(payload, cts.Token);
        await reader;

        loss.DroppedSeq.Should().BeGreaterThan(0, "the shim must have actually dropped a data packet");
        received.Should().Equal(payload, "retransmission must recover the dropped packet byte-exact");
    }
}
