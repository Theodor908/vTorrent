using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;
using vTorrent.Core.Engine;
using vTorrent.Core.Network;
using vTorrent.Core.PeerCommunication.Encryption;
using vTorrent.Core.PeerCommunication.Encryption.Primitives;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Transport.Utp;
using Xunit;

namespace vTorrent.Tests.Integration;

/// <summary>
/// TODO 0 (UTP_COMPLETION_HANDOFF): proves the full encrypted MSE/PE handshake and
/// bidirectional data transfer ride a real uTP connection end-to-end on loopback.
/// This is the regression that locks in the SYN-ACK off-by-one fix in
/// <see cref="UtpSocket.ProcessState"/> (see docs/UTP_STACK_FINDINGS.md): before the
/// fix the responder->initiator MSE key (Yb) never surfaced and the handshake hung.
/// </summary>
public class MseOverUtpTests
{
    private static readonly byte[] InfoHash =
        SHA1.HashData(Encoding.ASCII.GetBytes("mse-over-utp-test-torrent"));

    private static readonly byte[] OutboundPeerId =
        Encoding.ASCII.GetBytes("-VT0100-outbound0001");

    private static EncryptionSettings Rc4Settings() => new()
    {
        OutPolicy = EncryptionPolicy.Enabled,
        InPolicy = EncryptionPolicy.Enabled,
        AllowedLevel = EncryptionLevel.RC4
    };

    private static async Task<(UtpSocketManager u1, UtpSocketManager u2,
        UtpSocket a, UtpSocket b, UdpSocketManager d1, UdpSocketManager d2)>
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

    private static async Task ReadExactAsync(MseTransportStream s, byte[] buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(total), ct);
            if (n == 0) throw new InvalidOperationException("unexpected EOF during read");
            total += n;
        }
    }

    [Fact]
    public async Task EncryptedHandshake_And_BidirectionalTransfer_OverUtp()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (u1, u2, a, b, d1, d2) = await ConnectPairAsync(cts.Token);
        using var _ = u1; using var __ = u2; using var ___ = d1; using var ____ = d2;

        var streamA = new UtpTransportStream(a); // initiator / outbound
        var streamB = new UtpTransportStream(b); // responder / inbound

        var settings = Rc4Settings();
        var monitor = new OptionsMonitorShim<EncryptionSettings>(settings);
        var logger = NullLoggerFactory.Instance.CreateLogger<MseNegotiator>();

        byte[]? Req2Lookup(byte[] req2Hash) =>
            req2Hash.AsSpan().SequenceEqual(MseKeyDerivation.ComputeReq2Hash(InfoHash))
                ? InfoHash
                : null;

        // Run the real MSE/PE handshake concurrently over the two uTP streams.
        var outboundTask = MseTransportStream.CreateOutboundAsync(
            streamA, InfoHash, OutboundPeerId, monitor, logger, cts.Token);
        var inboundTask = MseTransportStream.CreateInboundAsync(
            streamB, Req2Lookup, monitor, logger, cts.Token);

        var mseA = await outboundTask;
        var mseB = await inboundTask;

        using (mseA)
        using (mseB)
        {
            // Both sides negotiated encryption at the same level.
            mseA.IsEncrypted.Should().BeTrue("outbound MSE over uTP must encrypt");
            mseB.IsEncrypted.Should().BeTrue("inbound MSE over uTP must encrypt");
            mseA.NegotiatedLevel.Should().Be(EncryptionLevel.RC4);
            mseB.NegotiatedLevel.Should().Be(EncryptionLevel.RC4);
            mseB.IdentifiedInfoHash.Should().Equal(InfoHash);

            // The outbound negotiator sends the BitTorrent handshake as MSE "IA" (initial
            // application) payload; the responder buffers it and delivers it before any
            // subsequent stream data. Drain + verify it first — this also proves the IA
            // itself rode uTP correctly (decrypts byte-exact).
            mseA.InitialPayloadSent.Should().BeTrue();
            var expectedIa = Handshake.CreateWithExtensions(InfoHash, OutboundPeerId, supportDHT: true).ToBytes();
            var iaOnB = new byte[expectedIa.Length];
            await ReadExactAsync(mseB, iaOnB, cts.Token);
            iaOnB.Should().Equal(expectedIa, "the BT handshake IA must decrypt byte-exact over uTP");

            // Push a few KB through the encrypted streams, both ways, and assert round-trip.
            var aToB = new byte[8 * 1024];
            var bToA = new byte[6 * 1024];
            Random.Shared.NextBytes(aToB);
            Random.Shared.NextBytes(bToA);

            var recvOnB = new byte[aToB.Length];
            var recvOnA = new byte[bToA.Length];

            var writeAB = mseA.WriteAsync(aToB, cts.Token).AsTask();
            var readAB = ReadExactAsync(mseB, recvOnB, cts.Token);
            await Task.WhenAll(writeAB, readAB);

            var writeBA = mseB.WriteAsync(bToA, cts.Token).AsTask();
            var readBA = ReadExactAsync(mseA, recvOnA, cts.Token);
            await Task.WhenAll(writeBA, readBA);

            recvOnB.Should().Equal(aToB, "A->B payload must decrypt byte-exact over uTP");
            recvOnA.Should().Equal(bToA, "B->A payload must decrypt byte-exact over uTP");
        }
    }
}
