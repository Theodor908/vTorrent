using FluentAssertions;
using vTorrent.Core.Settings;
using Xunit;

namespace vTorrent.Core.Tests.Settings;

public class SettingsSeederTests
{
    [Fact]
    public void CreateDefaults_AllSubSettingsNonNull()
    {
        var settings = SettingsSeeder.CreateDefaults();
        settings.Connection.Should().NotBeNull();
        settings.Bandwidth.Should().NotBeNull();
        settings.Protocol.Should().NotBeNull();
        settings.Dht.Should().NotBeNull();
        settings.Disk.Should().NotBeNull();
        settings.Queue.Should().NotBeNull();
        settings.Behavior.Should().NotBeNull();
        settings.Tracker.Should().NotBeNull();
        settings.Peer.Should().NotBeNull();
        settings.Encryption.Should().NotBeNull();
        settings.AutoSave.Should().NotBeNull();
        settings.Logging.Should().NotBeNull();
        settings.UI.Should().NotBeNull();
        settings.WebSeed.Should().NotBeNull();
        settings.Privacy.Should().NotBeNull();
        settings.Proxy.Should().NotBeNull();
        settings.Vpn.Should().NotBeNull();
    }

    [Fact]
    public void CreateDefaults_DhtSearchBranching_Is5()
    {
        SettingsSeeder.CreateDefaults().Dht.SearchBranching.Should().Be(5);
    }

    [Fact]
    public void CreateDefaults_PeerConnectTimeout_Is15()
    {
        SettingsSeeder.CreateDefaults().Peer.ConnectTimeout.Should().Be(15);
    }

    [Fact]
    public void CreateDefaults_TrackerNumWant_Is200()
    {
        SettingsSeeder.CreateDefaults().Tracker.NumWant.Should().Be(200);
    }

    [Fact]
    public void CreateDefaults_RoundTripsToJson()
    {
        var settings = SettingsSeeder.CreateDefaults();
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<vTorrent.Abstractions.Settings.GlobalSettings>(json);
        deserialized.Should().NotBeNull();
        deserialized!.Dht.SearchBranching.Should().Be(5);
        deserialized.Peer.ConnectTimeout.Should().Be(15);
    }
}
