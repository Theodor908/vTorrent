using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;
using vTorrent.Core.Engine;
using vTorrent.Core.Settings;
using vTorrent.Core.PeerCommunication.Encryption;
using vTorrent.Core.PeerCommunication.Encryption.Primitives;
using vTorrent.Core.PeerCommunication.Transport;
using vTorrent.Tests.Helpers;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication.Encryption;

public class MseTransportStreamTests
{
    private static readonly byte[] TestPeerId =
        System.Text.Encoding.ASCII.GetBytes("-VT0100-012345678901");
    private static readonly byte[] TestInfoHash = System.Security.Cryptography.SHA1.HashData(
        System.Text.Encoding.ASCII.GetBytes("test-torrent"));

    [Fact]
    public async Task EncryptedRoundTrip_DataIntegrity()
    {
        var (initiator, responder) = await CreateNegotiatedPairAsync(EncryptionLevel.RC4);

        // Drain the buffered IA payload (68-byte BT handshake embedded during MSE negotiation)
        await DrainBufferedPayloadAsync(responder);

        var message = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };
        await initiator.WriteAsync(message);

        var received = new byte[message.Length];
        int totalRead = 0;
        while (totalRead < received.Length)
        {
            int read = await responder.ReadAsync(received.AsMemory(totalRead));
            totalRead += read;
        }

        received.Should().Equal(message, "encrypted round-trip should preserve data");
    }

    [Fact]
    public async Task PlaintextPassthrough_NoTransformation()
    {
        var (initiator, responder) = await CreateNegotiatedPairAsync(EncryptionLevel.Plaintext);

        // Drain the buffered IA payload
        await DrainBufferedPayloadAsync(responder);

        var message = new byte[] { 0x01, 0x02, 0x03 };
        await initiator.WriteAsync(message);

        var received = new byte[3];
        int totalRead = 0;
        while (totalRead < received.Length)
        {
            int read = await responder.ReadAsync(received.AsMemory(totalRead));
            totalRead += read;
        }

        received.Should().Equal(message);
    }

    /// <summary>
    /// Drain any buffered InitialPayload (IA data) from the responder stream.
    /// The initiator embeds a 68-byte BT handshake as IA during MSE negotiation.
    /// </summary>
    private static async Task DrainBufferedPayloadAsync(MseTransportStream stream)
    {
        // Read up to 68 bytes (BT handshake size) — may be less if plaintext detect buffered 1 byte
        var drain = new byte[68];
        await stream.ReadAsync(drain);
    }

    [Fact]
    public void Properties_DelegateToInnerStream()
    {
        var inner = new FakeTransportStream();
        var result = MseResult.Plaintext();
        var stream = new MseTransportStream(inner, result);

        stream.IsConnected.Should().Be(inner.IsConnected);
        stream.RemoteEndPoint.Should().Be(inner.RemoteEndPoint);
        stream.TransportType.Should().Be(inner.TransportType);
    }

    private async Task<(MseTransportStream initiator, MseTransportStream responder)>
        CreateNegotiatedPairAsync(EncryptionLevel level)
    {
        var (initTransport, respTransport) = DuplexMemoryStream.CreatePair();

        var settings = new EncryptionSettings
        {
            OutPolicy = EncryptionPolicy.Enabled,
            InPolicy = EncryptionPolicy.Enabled,
            AllowedLevel = level
        };

        var logger = new NullLogger<MseNegotiator>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        byte[]? Req2Lookup(byte[] hash)
        {
            var expected = MseKeyDerivation.ComputeReq2Hash(TestInfoHash);
            return hash.AsSpan().SequenceEqual(expected) ? TestInfoHash : null;
        }

        var monitor = new OptionsMonitorShim<EncryptionSettings>(settings);
        var initTask = MseTransportStream.CreateOutboundAsync(
            initTransport, TestInfoHash, TestPeerId, monitor, logger, cts.Token);
        var respTask = MseTransportStream.CreateInboundAsync(
            respTransport, Req2Lookup, monitor, logger, cts.Token);

        await Task.WhenAll(initTask, respTask);

        return (initTask.Result, respTask.Result);
    }
}

internal class FakeTransportStream : ITransportStream
{
    public bool IsConnected => true;
    public EndPoint? RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 12345);
    public TransportType TransportType => TransportType.Tcp;
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => new(0);
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}
