using FluentAssertions;
using Xunit;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Network.I2P;
using vTorrent.Core.PeerCommunication.Transport.I2P;
using vTorrent.Core.Tests.Network.I2P;

namespace vTorrent.Core.Tests.Integration;

public class I2pIntegrationTests : IAsyncLifetime
{
    private MockSamBridge _bridge = null!;
    private string _tempDir = null!;

    public async Task InitializeAsync()
    {
        _bridge = new MockSamBridge();
        _bridge.SetDefaultHandshake();
        _bridge.SetDefaultSessionCreate("TEST_DEST");
        _bridge.SetDefaultDestGenerate(
            Convert.ToBase64String(new byte[32]),
            Convert.ToBase64String(new byte[32]));
        await _bridge.StartAsync();
        _tempDir = Path.Combine(Path.GetTempPath(), $"i2p_int_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task I2pSession_EstablishesAndExposesDest()
    {
        var settings = new I2pSettings
        {
            SamHostname = "127.0.0.1",
            SamPort = _bridge.Port,
            DestinationMode = I2pDestinationMode.SessionTransient
        };

        var session = new I2pSamSession(settings, _tempDir);
        await session.ConnectAsync();

        session.IsConnected.Should().BeTrue();
        session.SessionId.Should().NotBeNullOrEmpty();
        session.SamHostname.Should().Be("127.0.0.1");
        session.SamPort.Should().Be(_bridge.Port);

        await session.DisconnectAsync();
        session.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task I2pTransportConnector_CanBeCreatedFromSession()
    {
        var settings = new I2pSettings
        {
            SamHostname = "127.0.0.1",
            SamPort = _bridge.Port,
            DestinationMode = I2pDestinationMode.SessionTransient
        };

        var session = new I2pSamSession(settings, _tempDir);
        await session.ConnectAsync();

        var connector = new I2pTransportConnector(session);
        connector.Should().NotBeNull();

        // Verify it rejects clearnet endpoints
        var clearnetEp = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6881);
        var act = () => connector.ConnectAsync(clearnetEp);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*I2pTransportConnector only supports I2pEndPoint*");

        await session.DisconnectAsync();
    }

    [Fact]
    public async Task I2pHealthMonitor_TransitionsToAvailable()
    {
        var settings = new I2pSettings
        {
            SamHostname = "127.0.0.1",
            SamPort = _bridge.Port,
            DestinationMode = I2pDestinationMode.SessionTransient
        };

        var session = new I2pSamSession(settings, _tempDir);
        await session.ConnectAsync();

        var monitor = new I2pHealthMonitor(session, heartbeatInterval: TimeSpan.FromMilliseconds(100));
        monitor.Availability.Should().Be(I2pAvailability.Unavailable);
        monitor.Start();

        await Task.Delay(500);
        monitor.Availability.Should().Be(I2pAvailability.Available);

        await monitor.DisposeAsync();
        await session.DisconnectAsync();
    }

    [Fact]
    public void ManagedTorrent_ForceI2p_SetsIsI2p()
    {
        var mt = new vTorrent.Core.Orchestration.ManagedTorrent("abc123", "TestTorrent");
        mt.IsI2p.Should().BeFalse();
        mt.ForceI2p = true;
        mt.IsI2p.Should().BeTrue();
    }

    [Fact]
    public void I2pDestination_FullRoundTrip()
    {
        // Create from hash
        var hash = new byte[32];
        for (int i = 0; i < 32; i++) hash[i] = (byte)(i * 7);
        var dest = I2pDestination.FromHash(hash);

        // To compact and back
        var compact = dest.ToCompact();
        var restored = I2pDestination.FromCompact(compact);
        restored.Should().Be(dest);

        // To base32
        var b32 = dest.ToBase32();
        b32.Should().EndWith(".b32.i2p");

        // PeerInfo round trip
        var peer = PeerInfo.FromI2p(dest, "tracker");
        peer.IsI2p.Should().BeTrue();
        peer.NetworkEndPoint.Should().BeOfType<I2pEndPoint>();
        peer.DisplayAddress.Should().Contain("...");

        var peerCompact = peer.ToCompactFormatI2p();
        var restoredPeer = PeerInfo.FromCompactFormatI2p(peerCompact);
        restoredPeer.Destination.Should().Be(dest);
    }
}
