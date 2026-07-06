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
/// TODO 2 (UTP_COMPLETION_HANDOFF): send-window (flow control) enforcement.
/// A bulk writer pushes ~2 MB through a receiver whose reader is deliberately slow, i.e.
/// far more data than the receive window (256 * 1400 ≈ 358 KB). Correct BEP 29 flow
/// control requires: the receiver's advertised window shrinks as its buffer fills, the
/// sender blocks instead of blasting all data into flight (bounded memory), and the
/// window reopens as the reader drains so the transfer completes byte-exact.
/// </summary>
public class UtpFlowControlTests
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

    [Fact]
    public async Task SlowReader_BackpressuresSender_TransferCompletesBounded()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var (u1, u2, a, b, d1, d2) = await ConnectPairAsync(cts.Token);
        using var _ = u1; using var __ = u2; using var ___ = d1; using var ____ = d2;
        using var A = new UtpTransportStream(a); // bulk writer
        using var B = new UtpTransportStream(b); // slow reader

        const int total = 2 * 1024 * 1024; // 2 MB — ~6x the receive window
        var payload = new byte[total];
        new Random(1234).NextBytes(payload);
        var received = new byte[total];

        // Sample the sender's view of the peer window + its own bytes-in-flight while the
        // transfer runs, to prove flow control actually engaged and stayed bounded.
        uint minPeerWindow = uint.MaxValue;
        int maxInFlight = 0;
        using var sampleCts = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampleCts.IsCancellationRequested)
            {
                minPeerWindow = Math.Min(minPeerWindow, a.PeerAdvertisedWindow);
                maxInFlight = Math.Max(maxInFlight, a.BytesInFlight);
                try { await Task.Delay(5, sampleCts.Token); } catch { break; }
            }
        });

        // Slow reader: read in 8 KB chunks with a small delay so the receive buffer fills
        // and the advertised window is forced to shrink.
        var reader = Task.Run(async () =>
        {
            int off = 0;
            while (off < total)
            {
                int n = await B.ReadAsync(received.AsMemory(off, Math.Min(8192, total - off)), cts.Token);
                if (n == 0) throw new InvalidOperationException("unexpected EOF");
                off += n;
                if ((off / 8192) % 4 == 0)
                    await Task.Delay(2, cts.Token);
            }
        });

        await A.WriteAsync(payload, cts.Token);
        await reader;
        sampleCts.Cancel();
        try { await sampler; } catch { /* ignore */ }

        received.Should().Equal(payload, "flow-controlled transfer must arrive byte-exact");

        // The receiver advertised a window well below its full capacity at some point,
        // i.e. backpressure genuinely engaged.
        minPeerWindow.Should().BeLessThan(256u * UtpSocket.MaxPayloadSize / 2,
            "the slow reader must have forced the advertised window to shrink");

        // The sender never let unbounded data into flight — it stayed within a few receive
        // windows rather than buffering the whole 2 MB.
        maxInFlight.Should().BeLessThan(2 * 256 * UtpSocket.MaxPayloadSize,
            "bytes in flight must stay bounded by the window, not grow with the payload");
    }
}
