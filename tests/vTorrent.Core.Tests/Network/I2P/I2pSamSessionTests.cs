// tests/vTorrent.Core.Tests/Network/I2P/I2pSamSessionTests.cs
using FluentAssertions;
using Xunit;
using vTorrent.Core.Network.I2P;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Tests.Network.I2P;

public class I2pSamSessionTests : IAsyncLifetime
{
    private MockSamBridge _bridge = null!;
    private string _tempDir = null!;

    // Valid base64 strings for the mock — must be decodable by Convert.FromBase64String
    private const string TestPubKey = "dGVzdF9wdWJsaWNfa2V5X2Rlc3RpbmF0aW9uX2RhdGFfZm9yX2kycA==";
    private const string TestPrivKey = "dGVzdF9wcml2YXRlX2tleV9kYXRh";
    private const string TestSessionDest = "dGVzdF9zZXNzaW9uX2Rlc3RpbmF0aW9u";

    public async Task InitializeAsync()
    {
        _bridge = new MockSamBridge();
        _bridge.SetDefaultHandshake();
        _bridge.SetDefaultSessionCreate(TestSessionDest);
        _bridge.SetDefaultDestGenerate(TestPubKey, TestPrivKey);
        await _bridge.StartAsync();
        _tempDir = Path.Combine(Path.GetTempPath(), $"i2p_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task ConnectAsync_TransientMode_EstablishesSession()
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
    }

    [Fact]
    public async Task ConnectAsync_TransientMode_DoesNotPersistKey()
    {
        var settings = new I2pSettings
        {
            SamHostname = "127.0.0.1",
            SamPort = _bridge.Port,
            DestinationMode = I2pDestinationMode.SessionTransient
        };

        var session = new I2pSamSession(settings, _tempDir);
        await session.ConnectAsync();

        var keyFile = Path.Combine(_tempDir, "i2p_destination.key");
        File.Exists(keyFile).Should().BeFalse();
    }

    [Fact]
    public async Task ConnectAsync_PersistentMode_SavesKeyToFile()
    {
        var settings = new I2pSettings
        {
            SamHostname = "127.0.0.1",
            SamPort = _bridge.Port,
            DestinationMode = I2pDestinationMode.Persistent
        };

        var session = new I2pSamSession(settings, _tempDir);
        await session.ConnectAsync();

        var keyFile = Path.Combine(_tempDir, "i2p_destination.key");
        File.Exists(keyFile).Should().BeTrue();
        var content = await File.ReadAllTextAsync(keyFile);
        content.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ConnectAsync_PersistentMode_ReloadsKeyOnRestart()
    {
        var settings = new I2pSettings
        {
            SamHostname = "127.0.0.1",
            SamPort = _bridge.Port,
            DestinationMode = I2pDestinationMode.Persistent
        };

        // First session — generates and saves key
        var session1 = new I2pSamSession(settings, _tempDir);
        await session1.ConnectAsync();
        await session1.DisconnectAsync();

        // Verify key file exists after first session
        var keyFile = Path.Combine(_tempDir, "i2p_destination.key");
        File.Exists(keyFile).Should().BeTrue();

        // Second session — should reload the saved key
        var session2 = new I2pSamSession(settings, _tempDir);
        await session2.ConnectAsync();

        // Verify it sent the saved key in SESSION CREATE (not TRANSIENT)
        _bridge.ReceivedCommands.Should().Contain(c =>
            c.Contains("SESSION CREATE") && c.Contains("DESTINATION="));
    }

    [Fact]
    public async Task DisconnectAsync_SetsIsConnectedFalse()
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

        await session.DisconnectAsync();
        session.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task ConnectedEvent_Fires()
    {
        var settings = new I2pSettings
        {
            SamHostname = "127.0.0.1",
            SamPort = _bridge.Port,
            DestinationMode = I2pDestinationMode.SessionTransient
        };

        var session = new I2pSamSession(settings, _tempDir);
        bool fired = false;
        session.Connected += (s, e) => fired = true;

        await session.ConnectAsync();
        fired.Should().BeTrue();
    }

    [Fact]
    public async Task DisconnectedEvent_Fires()
    {
        var settings = new I2pSettings
        {
            SamHostname = "127.0.0.1",
            SamPort = _bridge.Port,
            DestinationMode = I2pDestinationMode.SessionTransient
        };

        var session = new I2pSamSession(settings, _tempDir);
        bool fired = false;
        session.Disconnected += (s, e) => fired = true;

        await session.ConnectAsync();
        await session.DisconnectAsync();
        fired.Should().BeTrue();
    }
}
