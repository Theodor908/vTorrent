using FluentAssertions;
using vTorrent.Abstractions.Settings;
using vTorrent.Desktop.ViewModels.Settings;
using Xunit;

namespace vTorrent.Tests.Unit.ViewModels;

public class BandwidthUnitHelperTests
{
    [Fact]
    public void BytesToDisplayUnit_KB_ShouldDivideBy1024()
    {
        BandwidthUnitHelper.BytesToDisplayUnit(1024, "KB/s").Should().Be(1.0);
    }

    [Fact]
    public void BytesToDisplayUnit_MB_ShouldDivideBy1048576()
    {
        BandwidthUnitHelper.BytesToDisplayUnit(1048576, "MB/s").Should().Be(1.0);
    }

    [Fact]
    public void BytesToDisplayUnit_GB_ShouldDivideBy1073741824()
    {
        BandwidthUnitHelper.BytesToDisplayUnit(1073741824, "GB/s").Should().Be(1.0);
    }

    [Fact]
    public void BytesToDisplayUnit_Zero_ShouldReturnZero()
    {
        BandwidthUnitHelper.BytesToDisplayUnit(0, "KB/s").Should().Be(0.0);
        BandwidthUnitHelper.BytesToDisplayUnit(0, "MB/s").Should().Be(0.0);
    }

    [Fact]
    public void DisplayUnitToBytes_KB_ShouldMultiplyBy1024()
    {
        BandwidthUnitHelper.DisplayUnitToBytes(1.0, "KB/s").Should().Be(1024);
    }

    [Fact]
    public void DisplayUnitToBytes_MB_ShouldMultiplyBy1048576()
    {
        BandwidthUnitHelper.DisplayUnitToBytes(1.0, "MB/s").Should().Be(1048576);
    }

    [Fact]
    public void DisplayUnitToBytes_GB_ShouldMultiplyBy1073741824()
    {
        BandwidthUnitHelper.DisplayUnitToBytes(1.0, "GB/s").Should().Be(1073741824);
    }

    [Fact]
    public void DisplayUnitToBytes_Zero_ShouldReturnZero()
    {
        BandwidthUnitHelper.DisplayUnitToBytes(0, "MB/s").Should().Be(0);
    }

    [Fact]
    public void RoundTrip_KB_ShouldPreserveValue()
    {
        var bytes = 512000;
        var display = BandwidthUnitHelper.BytesToDisplayUnit(bytes, "KB/s");
        var back = BandwidthUnitHelper.DisplayUnitToBytes(display, "KB/s");
        back.Should().Be(bytes);
    }

    [Fact]
    public void RoundTrip_MB_ShouldPreserveValue()
    {
        var bytes = 5 * 1024 * 1024;
        var display = BandwidthUnitHelper.BytesToDisplayUnit(bytes, "MB/s");
        var back = BandwidthUnitHelper.DisplayUnitToBytes(display, "MB/s");
        back.Should().Be(bytes);
    }

    [Fact]
    public void DetectBestUnit_SmallValues_ShouldReturnKB()
    {
        BandwidthUnitHelper.DetectBestUnit(1024, 2048).Should().Be("KB/s");
    }

    [Fact]
    public void DetectBestUnit_MediumValues_ShouldReturnMB()
    {
        BandwidthUnitHelper.DetectBestUnit(1048576, 0).Should().Be("MB/s");
    }

    [Fact]
    public void DetectBestUnit_LargeValues_ShouldReturnGB()
    {
        BandwidthUnitHelper.DetectBestUnit(1073741824, 0).Should().Be("GB/s");
    }

    [Fact]
    public void DetectBestUnit_BothZero_ShouldReturnKB()
    {
        BandwidthUnitHelper.DetectBestUnit(0, 0).Should().Be("KB/s");
    }
}

public class PropagatableFieldTrackerTests
{
    private static GlobalSettings CreateSettings(
        int perTorrentUpload = 0,
        int perTorrentDownload = 0,
        int maxConnsPerTorrent = 3,
        int maxUploadsPerTorrent = 4,
        float seedRatioLimit = 0f,
        int seedTimeLimit = 0,
        bool removeOnSeedComplete = false,
        bool pauseOnSeedComplete = false)
    {
        var s = new GlobalSettings();
        s.Bandwidth.PerTorrentUploadLimit = perTorrentUpload;
        s.Bandwidth.PerTorrentDownloadLimit = perTorrentDownload;
        s.Connection.MaxConnectionsPerTorrent = maxConnsPerTorrent;
        s.Connection.MaxUploadsPerTorrent = maxUploadsPerTorrent;
        s.Behavior.SeedRatioLimit = seedRatioLimit;
        s.Behavior.SeedTimeLimit = seedTimeLimit;
        s.Behavior.RemoveOnSeedComplete = removeOnSeedComplete;
        s.Behavior.PauseOnSeedComplete = pauseOnSeedComplete;
        return s;
    }

    [Fact]
    public void SnapshotOriginals_HasNoChanges()
    {
        var tracker = SettingsWindowViewModel.CreateTrackerForTesting();
        tracker.SnapshotOriginals(CreateSettings());
        tracker.HasChanges.Should().BeFalse();
        tracker.HasChangesInGroup(SettingsWindowViewModel.PropagatableFieldTracker.SettingsGroup.PerTorrentBandwidth).Should().BeFalse();
        tracker.HasChangesInGroup(SettingsWindowViewModel.PropagatableFieldTracker.SettingsGroup.PerTorrentConnection).Should().BeFalse();
        tracker.HasChangesInGroup(SettingsWindowViewModel.PropagatableFieldTracker.SettingsGroup.SeedingRules).Should().BeFalse();
    }

    [Fact]
    public void OnPropertyChanged_DifferentValue_MarksDirty()
    {
        var tracker = SettingsWindowViewModel.CreateTrackerForTesting();
        tracker.SnapshotOriginals(CreateSettings(perTorrentUpload: 1024));
        tracker.OnPropertyChanged("PerTorrentUploadLimit", 2048);
        tracker.HasChanges.Should().BeTrue();
        tracker.HasChangesInGroup(SettingsWindowViewModel.PropagatableFieldTracker.SettingsGroup.PerTorrentBandwidth).Should().BeTrue();
        tracker.HasChangesInGroup(SettingsWindowViewModel.PropagatableFieldTracker.SettingsGroup.PerTorrentConnection).Should().BeFalse();
    }

    [Fact]
    public void OnPropertyChanged_SameValue_StaysClean()
    {
        var tracker = SettingsWindowViewModel.CreateTrackerForTesting();
        tracker.SnapshotOriginals(CreateSettings(perTorrentUpload: 1024));
        tracker.OnPropertyChanged("PerTorrentUploadLimit", 1024);
        tracker.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void OnPropertyChanged_RevertToOriginal_BecomesClean()
    {
        var tracker = SettingsWindowViewModel.CreateTrackerForTesting();
        tracker.SnapshotOriginals(CreateSettings(perTorrentUpload: 1024));
        tracker.OnPropertyChanged("PerTorrentUploadLimit", 2048);
        tracker.HasChanges.Should().BeTrue();
        tracker.OnPropertyChanged("PerTorrentUploadLimit", 1024);
        tracker.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void GetChangesForGroup_ReturnsOnlyChanged()
    {
        var tracker = SettingsWindowViewModel.CreateTrackerForTesting();
        tracker.SnapshotOriginals(CreateSettings(perTorrentUpload: 1024, perTorrentDownload: 2048));
        tracker.OnPropertyChanged("PerTorrentUploadLimit", 4096);
        tracker.OnPropertyChanged("PerTorrentDownloadLimit", 2048);
        var changes = tracker.GetChangesForGroup(SettingsWindowViewModel.PropagatableFieldTracker.SettingsGroup.PerTorrentBandwidth);
        changes.Should().HaveCount(1);
        changes[0].settingName.Should().Be("PerTorrentUploadLimit");
        changes[0].original.Should().Be(1024);
        changes[0].current.Should().Be(4096);
    }

    [Fact]
    public void ResetGroup_ClearsOnlyThatGroup()
    {
        var tracker = SettingsWindowViewModel.CreateTrackerForTesting();
        tracker.SnapshotOriginals(CreateSettings(perTorrentUpload: 1024, maxConnsPerTorrent: 50));
        tracker.OnPropertyChanged("PerTorrentUploadLimit", 2048);
        tracker.OnPropertyChanged("MaxConnectionsPerTorrent", 100);
        tracker.HasChanges.Should().BeTrue();
        var newSettings = CreateSettings(perTorrentUpload: 2048, maxConnsPerTorrent: 100);
        tracker.ResetGroup(SettingsWindowViewModel.PropagatableFieldTracker.SettingsGroup.PerTorrentBandwidth, newSettings);
        tracker.HasChangesInGroup(SettingsWindowViewModel.PropagatableFieldTracker.SettingsGroup.PerTorrentBandwidth).Should().BeFalse();
        tracker.HasChangesInGroup(SettingsWindowViewModel.PropagatableFieldTracker.SettingsGroup.PerTorrentConnection).Should().BeTrue();
    }

    [Fact]
    public void ResetAll_ViaSnapshotOriginals_ClearsEverything()
    {
        var tracker = SettingsWindowViewModel.CreateTrackerForTesting();
        tracker.SnapshotOriginals(CreateSettings(perTorrentUpload: 1024, seedRatioLimit: 1.0f));
        tracker.OnPropertyChanged("PerTorrentUploadLimit", 2048);
        tracker.OnPropertyChanged("SeedRatioLimit", (object)2.0f);
        tracker.HasChanges.Should().BeTrue();
        tracker.SnapshotOriginals(CreateSettings(perTorrentUpload: 2048, seedRatioLimit: 2.0f));
        tracker.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void MultipleSettingsInGroup_AllMustRevert()
    {
        var tracker = SettingsWindowViewModel.CreateTrackerForTesting();
        tracker.SnapshotOriginals(CreateSettings(perTorrentUpload: 1024, perTorrentDownload: 2048));
        tracker.OnPropertyChanged("PerTorrentUploadLimit", 4096);
        tracker.OnPropertyChanged("PerTorrentDownloadLimit", 8192);
        tracker.OnPropertyChanged("PerTorrentUploadLimit", 1024);
        tracker.HasChangesInGroup(SettingsWindowViewModel.PropagatableFieldTracker.SettingsGroup.PerTorrentBandwidth).Should().BeTrue();
        tracker.OnPropertyChanged("PerTorrentDownloadLimit", 2048);
        tracker.HasChangesInGroup(SettingsWindowViewModel.PropagatableFieldTracker.SettingsGroup.PerTorrentBandwidth).Should().BeFalse();
    }

    [Fact]
    public void SeedingRules_BooleanTracking()
    {
        var tracker = SettingsWindowViewModel.CreateTrackerForTesting();
        tracker.SnapshotOriginals(CreateSettings(pauseOnSeedComplete: false));
        tracker.OnPropertyChanged("PauseOnSeedComplete", (object)true);
        tracker.HasChangesInGroup(SettingsWindowViewModel.PropagatableFieldTracker.SettingsGroup.SeedingRules).Should().BeTrue();
        tracker.OnPropertyChanged("PauseOnSeedComplete", (object)false);
        tracker.HasChangesInGroup(SettingsWindowViewModel.PropagatableFieldTracker.SettingsGroup.SeedingRules).Should().BeFalse();
    }
}
