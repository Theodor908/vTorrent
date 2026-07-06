// tests/vTorrent.Core.Tests/Network/I2P/I2pSamClientTests.cs
using FluentAssertions;
using Xunit;
using vTorrent.Core.Network.I2P;

namespace vTorrent.Core.Tests.Network.I2P;

public class I2pSamClientTests : IAsyncLifetime
{
    private MockSamBridge _bridge = null!;

    public async Task InitializeAsync()
    {
        _bridge = new MockSamBridge();
        _bridge.SetDefaultHandshake();
        await _bridge.StartAsync();
    }

    public async Task DisposeAsync() => await _bridge.DisposeAsync();

    [Fact]
    public async Task Handshake_SendsHelloVersion()
    {
        await using var client = new I2pSamClient("127.0.0.1", _bridge.Port);
        var version = await client.HandshakeAsync();

        version.Should().Be("3.3");
        _bridge.ReceivedCommands.Should().Contain(c => c.StartsWith("HELLO VERSION"));
    }

    [Fact]
    public async Task GenerateDestination_ReturnsKeyPair()
    {
        _bridge.SetDefaultDestGenerate("PUB_KEY_B64", "PRIV_KEY_B64");
        await using var client = new I2pSamClient("127.0.0.1", _bridge.Port);
        await client.HandshakeAsync();

        var (pub, priv) = await client.GenerateDestinationAsync();
        pub.Should().Be("PUB_KEY_B64");
        priv.Should().Be("PRIV_KEY_B64");
    }

    [Fact]
    public async Task CreateSession_SendsCorrectCommand()
    {
        _bridge.SetDefaultSessionCreate();
        await using var client = new I2pSamClient("127.0.0.1", _bridge.Port);
        await client.HandshakeAsync();

        await client.CreateSessionAsync("test_session", "TRANSIENT", new I2pTunnelConfig());

        _bridge.ReceivedCommands.Should().Contain(c =>
            c.Contains("SESSION CREATE") &&
            c.Contains("STYLE=PRIMARY") &&
            c.Contains("ID=test_session") &&
            c.Contains("SIGNATURE_TYPE=7"));
    }

    [Fact]
    public async Task NamingLookup_ResolvesDestination()
    {
        _bridge.SetDefaultNamingLookup("test.b32.i2p", "RESOLVED_DEST_B64");
        await using var client = new I2pSamClient("127.0.0.1", _bridge.Port);
        await client.HandshakeAsync();

        var dest = await client.NamingLookupAsync("test.b32.i2p");
        dest.Should().Be("RESOLVED_DEST_B64");
    }

    [Fact]
    public async Task Handshake_BridgeDown_ThrowsOnConnect()
    {
        await _bridge.DisposeAsync();
        await using var client = new I2pSamClient("127.0.0.1", _bridge.Port);

        var act = () => client.HandshakeAsync();
        await act.Should().ThrowAsync<Exception>();
    }
}
