using FluentAssertions;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;
using Xunit;

namespace vTorrent.Core.Tests.Settings;

public class ProfileSettingsTests
{
    [Fact]
    public void DefaultConstructor_AllValuesMatchSettingsDefaults()
    {
        var values = new ProfileSettingsValues();

        // Bandwidth defaults
        values.GlobalDownloadLimit.Should().Be(0);
        values.GlobalUploadLimit.Should().Be(0);
        values.PerTorrentDownloadLimit.Should().Be(0);
        values.PerTorrentUploadLimit.Should().Be(0);
        values.MixedModeAlgorithm.Should().Be(MixedModeAlgorithm.PeerProportional);

        // Connection defaults
        values.MaxGlobalConnections.Should().Be(500);
        values.MaxConnectionsPerTorrent.Should().Be(200);
        values.MaxUploadsPerTorrent.Should().Be(4);
        values.MaxHalfOpenConnections.Should().Be(50);
        values.ConnectionSpeed.Should().Be(30);

        // Queue defaults
        values.MaxActiveDownloads.Should().Be(5);
        values.MaxActiveSeeds.Should().Be(-1);
        values.MaxActiveTorrents.Should().Be(10);
        values.DontCountSlowTorrents.Should().BeTrue();
        values.ConnectSeedEveryNDownload.Should().Be(10);

        // Choking defaults
        values.ChokingAlgorithm.Should().Be(ChokingAlgorithm.RateBased);
        values.SeedChokingAlgorithm.Should().Be(SeedChokingAlgorithm.FastestUpload);
        values.UnchokeSlots.Should().Be(8);
        values.UnchokeInterval.Should().Be(15);
        values.OptimisticUnchokeInterval.Should().Be(30);
        values.NumOptimisticUnchokeSlots.Should().Be(0);

        // Peer defaults
        values.PeerTurnover.Should().Be(4);
        values.PeerTurnoverCutoff.Should().Be(90);
        values.PeerTurnoverInterval.Should().Be(300);
        values.MaxPendingBlocksPerPeer.Should().Be(500);

        // Disk defaults
        values.BackendType.Should().Be(DiskBackendType.Auto);
        values.CacheSize.Should().Be(64 * 1024 * 1024);
        values.MaxOutstandingDiskRequests.Should().Be(64);
        values.HashThreads.Should().Be(2);

        // Seeding defaults
        values.SeedRatioLimit.Should().Be(0f);
        values.SeedTimeLimit.Should().Be(0);
        values.PauseOnSeedComplete.Should().BeFalse();
        values.RemoveOnSeedComplete.Should().BeFalse();

        // Picker defaults
        values.InitialPickerThreshold.Should().Be(4);
        values.WholePiecesThreshold.Should().Be(20);
    }

    [Fact]
    public void SnapshotFrom_CapturesAllValues()
    {
        var global = new GlobalSettings();
        global.Bandwidth.GlobalDownloadLimit = 1_000_000;
        global.Bandwidth.GlobalUploadLimit = 500_000;
        global.Connection.MaxGlobalConnections = 1000;
        global.Behavior.SeedRatioLimit = 2.5f;
        global.Disk.CacheSize = 128 * 1024 * 1024;
        global.Disk.HashThreads = 4;
        global.Peer.UnchokeInterval = 20;

        var snapshot = ProfileSettingsValues.SnapshotFrom(global);

        snapshot.GlobalDownloadLimit.Should().Be(1_000_000);
        snapshot.GlobalUploadLimit.Should().Be(500_000);
        snapshot.MaxGlobalConnections.Should().Be(1000);
        snapshot.SeedRatioLimit.Should().Be(2.5f);
        snapshot.CacheSize.Should().Be(128 * 1024 * 1024);
        snapshot.HashThreads.Should().Be(4);
        snapshot.UnchokeInterval.Should().Be(20);
    }

    [Fact]
    public void ApplyTo_WritesAllValues()
    {
        var values = new ProfileSettingsValues
        {
            GlobalDownloadLimit = 2_000_000,
            GlobalUploadLimit = 1_000_000,
            MaxGlobalConnections = 800,
            MaxConnectionsPerTorrent = 300,
            MaxUploadsPerTorrent = 6,
            MaxHalfOpenConnections = 100,
            ConnectionSpeed = 50,
            MaxActiveDownloads = 8,
            MaxActiveSeeds = 5,
            MaxActiveTorrents = 15,
            DontCountSlowTorrents = false,
            ConnectSeedEveryNDownload = 5,
            ChokingAlgorithm = ChokingAlgorithm.FixedSlots,
            SeedChokingAlgorithm = SeedChokingAlgorithm.RoundRobin,
            UnchokeSlots = 12,
            UnchokeInterval = 20,
            OptimisticUnchokeInterval = 45,
            NumOptimisticUnchokeSlots = 3,
            PeerTurnover = 6,
            PeerTurnoverCutoff = 80,
            PeerTurnoverInterval = 200,
            MaxPendingBlocksPerPeer = 300,
            BackendType = DiskBackendType.ForceMmap,
            CacheSize = 256 * 1024 * 1024,
            MaxOutstandingDiskRequests = 128,
            HashThreads = 4,
            SeedRatioLimit = 3.0f,
            SeedTimeLimit = 720,
            PauseOnSeedComplete = true,
            RemoveOnSeedComplete = true,
            InitialPickerThreshold = 8,
            WholePiecesThreshold = 30,
            MixedModeAlgorithm = MixedModeAlgorithm.PreferTcp,
            PerTorrentDownloadLimit = 100_000,
            PerTorrentUploadLimit = 50_000
        };

        var global = new GlobalSettings();
        values.ApplyTo(global);

        global.Bandwidth.GlobalDownloadLimit.Should().Be(2_000_000);
        global.Bandwidth.GlobalUploadLimit.Should().Be(1_000_000);
        global.Bandwidth.PerTorrentDownloadLimit.Should().Be(100_000);
        global.Bandwidth.PerTorrentUploadLimit.Should().Be(50_000);
        global.Bandwidth.MixedModeAlgorithm.Should().Be(MixedModeAlgorithm.PreferTcp);
        global.Connection.MaxGlobalConnections.Should().Be(800);
        global.Connection.MaxConnectionsPerTorrent.Should().Be(300);
        global.Connection.MaxUploadsPerTorrent.Should().Be(6);
        global.Connection.MaxHalfOpenConnections.Should().Be(100);
        global.Connection.ConnectionSpeed.Should().Be(50);
        global.Queue.MaxActiveDownloads.Should().Be(8);
        global.Queue.MaxActiveSeeds.Should().Be(5);
        global.Queue.MaxActiveTorrents.Should().Be(15);
        global.Queue.DontCountSlowTorrents.Should().BeFalse();
        global.Queue.ConnectSeedEveryNDownload.Should().Be(5);
        global.Behavior.ChokingAlgorithm.Should().Be(ChokingAlgorithm.FixedSlots);
        global.Behavior.SeedChokingAlgorithm.Should().Be(SeedChokingAlgorithm.RoundRobin);
        global.Behavior.UnchokeSlots.Should().Be(12);
        global.Peer.UnchokeInterval.Should().Be(20);
        global.Peer.OptimisticUnchokeInterval.Should().Be(45);
        global.Peer.NumOptimisticUnchokeSlots.Should().Be(3);
        global.Behavior.PeerTurnover.Should().Be(6);
        global.Behavior.PeerTurnoverCutoff.Should().Be(80);
        global.Behavior.PeerTurnoverInterval.Should().Be(200);
        global.Peer.MaxPendingBlocksPerPeer.Should().Be(300);
        global.Disk.BackendType.Should().Be(DiskBackendType.ForceMmap);
        global.Disk.CacheSize.Should().Be(256 * 1024 * 1024);
        global.Disk.MaxOutstandingDiskRequests.Should().Be(128);
        global.Disk.HashThreads.Should().Be(4);
        global.Behavior.SeedRatioLimit.Should().Be(3.0f);
        global.Behavior.SeedTimeLimit.Should().Be(720);
        global.Behavior.PauseOnSeedComplete.Should().BeTrue();
        global.Behavior.RemoveOnSeedComplete.Should().BeTrue();
        global.Behavior.InitialPickerThreshold.Should().Be(8);
        global.Behavior.WholePiecesThreshold.Should().Be(30);
    }

    [Fact]
    public void ValueEquals_IdenticalValues_ReturnsTrue()
    {
        var a = new ProfileSettingsValues();
        var b = new ProfileSettingsValues();

        a.ValueEquals(b).Should().BeTrue();
    }

    [Fact]
    public void ValueEquals_DriftInt_ReturnsFalse()
    {
        var a = new ProfileSettingsValues();
        var b = new ProfileSettingsValues { MaxGlobalConnections = 501 };

        a.ValueEquals(b).Should().BeFalse();
    }

    [Fact]
    public void ValueEquals_DriftFloat_WithinEpsilon_ReturnsTrue()
    {
        var a = new ProfileSettingsValues { SeedRatioLimit = 1.0f };
        var b = new ProfileSettingsValues { SeedRatioLimit = 1.0f + 1e-7f };

        a.ValueEquals(b).Should().BeTrue();
    }

    [Fact]
    public void ValueEquals_DriftFloat_OutsideEpsilon_ReturnsFalse()
    {
        var a = new ProfileSettingsValues { SeedRatioLimit = 1.0f };
        var b = new ProfileSettingsValues { SeedRatioLimit = 1.1f };

        a.ValueEquals(b).Should().BeFalse();
    }

    [Fact]
    public void SnapshotFrom_ThenApplyTo_RoundTrips()
    {
        var original = new GlobalSettings();
        original.Bandwidth.GlobalDownloadLimit = 999_999;
        original.Behavior.PeerTurnover = 7;
        original.Disk.HashThreads = 3;

        var snapshot = ProfileSettingsValues.SnapshotFrom(original);
        var target = new GlobalSettings();
        snapshot.ApplyTo(target);

        target.Bandwidth.GlobalDownloadLimit.Should().Be(999_999);
        target.Behavior.PeerTurnover.Should().Be(7);
        target.Disk.HashThreads.Should().Be(3);
    }
}
