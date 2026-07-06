using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;
using vTorrent.Core.Engine;
using vTorrent.Core.Settings;
using vTorrent.Core.PeerCommunication.Encryption;
using vTorrent.Core.PeerCommunication.Encryption.Primitives;
using vTorrent.Tests.Helpers;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication.Encryption;

public class MseNegotiatorTests
{
    private static readonly byte[] TestInfoHash = System.Security.Cryptography.SHA1.HashData(
        System.Text.Encoding.ASCII.GetBytes("test-torrent"));

    private static readonly ILogger<MseNegotiator> Logger =
        NullLoggerFactory.Instance.CreateLogger<MseNegotiator>();

    private static EncryptionSettings DefaultSettings => new()
    {
        OutPolicy = EncryptionPolicy.Enabled,
        InPolicy = EncryptionPolicy.Enabled,
        AllowedLevel = EncryptionLevel.Both
    };

    private static readonly byte[] TestPeerId =
        System.Text.Encoding.ASCII.GetBytes("-VT0100-012345678901");

    [Fact]
    public async Task FullHandshake_RC4_BothSidesSucceed()
    {
        var (initiatorStream, responderStream) = DuplexMemoryStream.CreatePair();

        var settings = DefaultSettings;
        settings.AllowedLevel = EncryptionLevel.RC4;

        byte[]? Req2Lookup(byte[] req2Hash)
        {
            var expected = MseKeyDerivation.ComputeReq2Hash(TestInfoHash);
            if (req2Hash.AsSpan().SequenceEqual(expected))
                return TestInfoHash;
            return null;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var initiatorNeg = new MseNegotiator(initiatorStream, new OptionsMonitorShim<EncryptionSettings>(settings), Logger);
        var responderNeg = new MseNegotiator(responderStream, new OptionsMonitorShim<EncryptionSettings>(settings), Logger);

        var initiatorTask = initiatorNeg.NegotiateOutboundAsync(TestInfoHash, TestPeerId, cts.Token);
        var responderTask = responderNeg.NegotiateInboundAsync(Req2Lookup, cts.Token);

        var results = await Task.WhenAll(initiatorTask, responderTask);
        var initResult = results[0];
        var respResult = results[1];

        initResult.IsEncrypted.Should().BeTrue();
        initResult.NegotiatedLevel.Should().Be(EncryptionLevel.RC4);
        initResult.OutgoingCipher.Should().NotBeNull();
        initResult.IncomingCipher.Should().NotBeNull();

        respResult.IsEncrypted.Should().BeTrue();
        respResult.NegotiatedLevel.Should().Be(EncryptionLevel.RC4);
        respResult.IdentifiedInfoHash.Should().Equal(TestInfoHash);
    }

    [Fact]
    public async Task FullHandshake_Plaintext_NegotiatesCorrectly()
    {
        var (initiatorStream, responderStream) = DuplexMemoryStream.CreatePair();

        var settings = DefaultSettings;
        settings.AllowedLevel = EncryptionLevel.Plaintext;

        byte[]? Req2Lookup(byte[] req2Hash)
        {
            var expected = MseKeyDerivation.ComputeReq2Hash(TestInfoHash);
            return req2Hash.AsSpan().SequenceEqual(expected) ? TestInfoHash : null;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var initiatorNeg = new MseNegotiator(initiatorStream, new OptionsMonitorShim<EncryptionSettings>(settings), Logger);
        var responderNeg = new MseNegotiator(responderStream, new OptionsMonitorShim<EncryptionSettings>(settings), Logger);

        var initiatorTask = initiatorNeg.NegotiateOutboundAsync(TestInfoHash, TestPeerId, cts.Token);
        var responderTask = responderNeg.NegotiateInboundAsync(Req2Lookup, cts.Token);

        var results = await Task.WhenAll(initiatorTask, responderTask);

        results[0].NegotiatedLevel.Should().Be(EncryptionLevel.Plaintext);
        results[0].OutgoingCipher.Should().BeNull("plaintext level means no RC4");
        results[1].NegotiatedLevel.Should().Be(EncryptionLevel.Plaintext);
    }

    [Fact]
    public async Task Inbound_PlaintextHandshake_WhenPolicyEnabled_ReturnsPlaintext()
    {
        var pipeOut = new AnonymousPipeServerStream(PipeDirection.Out);
        var pipeIn = new AnonymousPipeClientStream(PipeDirection.In, pipeOut.GetClientHandleAsString());
        var dummyPipe = new AnonymousPipeServerStream(PipeDirection.Out);

        var stream = new DuplexMemoryStream(readFrom: pipeIn, writeTo: dummyPipe);

        // Write plaintext handshake header byte
        await pipeOut.WriteAsync(new byte[] { 0x13 });
        await pipeOut.FlushAsync();

        var settings = new EncryptionSettings { InPolicy = EncryptionPolicy.Enabled };
        var negotiator = new MseNegotiator(stream, new OptionsMonitorShim<EncryptionSettings>(settings), Logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await negotiator.NegotiateInboundAsync(_ => null, cts.Token);

        result.IsEncrypted.Should().BeFalse();
        result.InitialPayload.Should().NotBeNull()
            .And.HaveCountGreaterOrEqualTo(1, "the peeked 0x13 byte should be buffered");
    }

    [Fact]
    public async Task Inbound_PlaintextHandshake_WhenPolicyForced_Throws()
    {
        var pipeOut = new AnonymousPipeServerStream(PipeDirection.Out);
        var pipeIn = new AnonymousPipeClientStream(PipeDirection.In, pipeOut.GetClientHandleAsString());
        var dummyPipe = new AnonymousPipeServerStream(PipeDirection.Out);

        var stream = new DuplexMemoryStream(readFrom: pipeIn, writeTo: dummyPipe);

        await pipeOut.WriteAsync(new byte[] { 0x13 });
        await pipeOut.FlushAsync();

        var settings = new EncryptionSettings { InPolicy = EncryptionPolicy.Forced };
        var negotiator = new MseNegotiator(stream, new OptionsMonitorShim<EncryptionSettings>(settings), Logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var act = () => negotiator.NegotiateInboundAsync(_ => null, cts.Token);
        await act.Should().ThrowAsync<MseNegotiationException>()
            .WithMessage("*plaintext*rejected*");
    }
}
