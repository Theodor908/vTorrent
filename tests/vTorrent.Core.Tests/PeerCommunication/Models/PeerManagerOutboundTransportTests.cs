using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings.Enums;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.Tests.PeerCommunication.Support;
using Xunit;

namespace vTorrent.Core.Tests.PeerCommunication.Models;

public class PeerManagerOutboundTransportTests
{
    private static PeerInfo LocalPeer() =>
        PeerInfo.FromEndPoint(new IPEndPoint(IPAddress.Loopback, 6881));

    [Fact]
    public async Task MseAttempt_RidesConnectorStream_ThenFallsBackToPlaintextOnFailure()
    {
        var first = new FakeDialStream();   // MSE will write DH here, then ReadAsync throws IOException
        var second = new FakeDialStream();  // plaintext reconnect target
        var connector = new RecordingTransportConnector(first, second);
        var pm = PeerManagerTestFactory.CreateWithConnector(connector, EncryptionPolicy.Enabled);
        var peer = LocalPeer();

        var (transport, isEncrypted, handshakeAlreadySent) =
            await pm.EstablishOutboundTransportAsync(peer, new byte[20], CancellationToken.None);

        connector.CallCount.Should().Be(2, "MSE failed → one fresh dial for the plaintext retry");
        first.Writes.Should().NotBeEmpty("the MSE DH key must be written to the CONNECTOR-provided stream, proving MSE rode the connector (not a raw socket)");
        first.Disposed.Should().BeTrue("the MSE-dirtied stream must be disposed before the plaintext reconnect");
        transport.Should().BeSameAs(second, "plaintext path uses the freshly dialed stream");
        isEncrypted.Should().BeFalse();
        peer.EncryptionSupport.Should().Be(MsePeerEncryptionSupport.Unsupported);
    }

    [Fact]
    public async Task ForcedPolicy_MseFailure_ThrowsWithoutReconnect()
    {
        var only = new FakeDialStream();
        var connector = new RecordingTransportConnector(only);
        var pm = PeerManagerTestFactory.CreateWithConnector(connector, EncryptionPolicy.Forced);

        Func<Task> act = () => pm.EstablishOutboundTransportAsync(LocalPeer(), new byte[20], CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        connector.CallCount.Should().Be(1, "Forced policy must not fall back / reconnect");
    }

    [Fact]
    public async Task KnownUnsupportedPeer_SkipsMse_SingleDial_PlaintextStream()
    {
        var only = new FakeDialStream();
        var connector = new RecordingTransportConnector(only);
        var pm = PeerManagerTestFactory.CreateWithConnector(connector, EncryptionPolicy.Enabled);
        var peer = LocalPeer();
        peer.EncryptionSupport = MsePeerEncryptionSupport.Unsupported;   // ShouldAttemptMse → false

        var (transport, isEncrypted, _) =
            await pm.EstablishOutboundTransportAsync(peer, new byte[20], CancellationToken.None);

        connector.CallCount.Should().Be(1);
        transport.Should().BeSameAs(only, "MSE skipped → the single dialed stream is returned directly");
        only.Writes.Should().BeEmpty("no MSE handshake bytes when MSE is skipped");
        isEncrypted.Should().BeFalse();
    }
}
