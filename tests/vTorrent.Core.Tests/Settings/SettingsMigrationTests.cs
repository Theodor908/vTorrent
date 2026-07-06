using FluentAssertions;
using vTorrent.Abstractions.Settings;
using Xunit;

namespace vTorrent.Core.Tests.Settings;

public class SettingsMigrationTests
{
    [Fact]
    public void V3Settings_Deserialize_PreservesOldValues()
    {
        var v3Json = """
        {
            "Version": 3,
            "Peer": {
                "ConnectTimeout": 10,
                "HandshakeTimeout": 10,
                "PieceTimeout": 20,
                "InactivityTimeout": 600
            },
            "Dht": {
                "SearchBranching": 3,
                "MaxPeersPerInfoHash": 2000,
                "EnforceNodeId": false
            }
        }
        """;

        var settings = System.Text.Json.JsonSerializer.Deserialize<GlobalSettings>(v3Json);
        settings.Should().NotBeNull();
        settings!.Peer.ConnectTimeout.Should().Be(10);
        settings.Peer.HandshakeTimeout.Should().Be(10);
        settings.Dht.SearchBranching.Should().Be(3);
    }

    [Fact]
    public void EmptyJson_Deserializes_WithNewDefaults()
    {
        var json = "{}";
        var settings = System.Text.Json.JsonSerializer.Deserialize<GlobalSettings>(json);
        settings.Should().NotBeNull();
        settings!.Peer.Should().NotBeNull();
        settings.Dht.Should().NotBeNull();
        settings.Peer.ConnectTimeout.Should().Be(15);
        settings.Dht.SearchBranching.Should().Be(5);
    }
}
