using FluentAssertions;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;
using Xunit;

namespace vTorrent.Core.Tests.Settings;

public class ProfilePresetsTests
{
    [Fact]
    public void All_HasThreePresets()
    {
        ProfilePresets.All.Should().HaveCount(3);
    }

    [Fact]
    public void Quiet_HasCorrectValues()
    {
        var quiet = ProfilePresets.Quiet;

        quiet.Name.Should().Be("Quiet");
        quiet.Color.Should().Be("#78909C");

        var s = quiet.Settings;
        s.GlobalDownloadLimit.Should().Be(1 * 1024 * 1024);
        s.GlobalUploadLimit.Should().Be(256 * 1024);
        s.MaxGlobalConnections.Should().Be(100);
        s.MaxConnectionsPerTorrent.Should().Be(50);
        s.MaxUploadsPerTorrent.Should().Be(2);
        s.MaxHalfOpenConnections.Should().Be(10);
        s.ConnectionSpeed.Should().Be(5);
        s.MaxActiveDownloads.Should().Be(2);
        s.MaxActiveSeeds.Should().Be(3);
        s.MaxActiveTorrents.Should().Be(4);
        s.ChokingAlgorithm.Should().Be(ChokingAlgorithm.RateBased);
        s.UnchokeSlots.Should().Be(4);
        s.MixedModeAlgorithm.Should().Be(MixedModeAlgorithm.PreferUtp);
        s.PeerTurnover.Should().Be(2);
        s.MaxPendingBlocksPerPeer.Should().Be(100);
        s.CacheSize.Should().Be(16 * 1024 * 1024);
        s.MaxOutstandingDiskRequests.Should().Be(16);
        s.HashThreads.Should().Be(1);
        s.SeedRatioLimit.Should().Be(1.0f);
        s.SeedTimeLimit.Should().Be(1440);
        s.PauseOnSeedComplete.Should().BeTrue();
    }

    [Fact]
    public void Balanced_MatchesDefaults()
    {
        var balanced = ProfilePresets.Balanced;
        var defaults = new ProfileSettingsValues();

        balanced.Name.Should().Be("Balanced");
        balanced.Color.Should().Be("#2196F3");
        balanced.Settings.ValueEquals(defaults).Should().BeTrue();
    }

    [Fact]
    public void Performance_HasCorrectValues()
    {
        var perf = ProfilePresets.Performance;

        perf.Name.Should().Be("Performance");
        perf.Color.Should().Be("#F44336");

        var s = perf.Settings;
        s.MaxGlobalConnections.Should().Be(2000);
        s.MaxConnectionsPerTorrent.Should().Be(500);
        s.MaxUploadsPerTorrent.Should().Be(-1);
        s.MaxHalfOpenConnections.Should().Be(200);
        s.ConnectionSpeed.Should().Be(200);
        s.MaxActiveDownloads.Should().Be(20);
        s.MaxActiveSeeds.Should().Be(-1);
        s.MaxActiveTorrents.Should().Be(-1);
        s.UnchokeSlots.Should().Be(-1);
        s.PeerTurnover.Should().Be(8);
        s.MixedModeAlgorithm.Should().Be(MixedModeAlgorithm.PreferTcp);
        s.CacheSize.Should().Be(512 * 1024 * 1024);
        s.MaxOutstandingDiskRequests.Should().Be(256);
        s.HashThreads.Should().Be(4);
    }

    [Theory]
    [InlineData("Quiet", true)]
    [InlineData("Balanced", true)]
    [InlineData("Performance", true)]
    [InlineData("Custom", false)]
    [InlineData("quiet", true)]
    [InlineData("", false)]
    public void IsBuiltIn_ReturnsExpected(string name, bool expected)
    {
        ProfilePresets.IsBuiltIn(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("Quiet")]
    [InlineData("Balanced")]
    [InlineData("Performance")]
    public void GetBuiltIn_ReturnsCorrectProfile(string name)
    {
        var profile = ProfilePresets.GetBuiltIn(name);
        profile.Should().NotBeNull();
        profile!.Name.Should().Be(name);
    }

    [Fact]
    public void GetBuiltIn_UnknownName_ReturnsNull()
    {
        ProfilePresets.GetBuiltIn("NonExistent").Should().BeNull();
    }
}
