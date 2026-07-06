using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Desktop.ViewModels.Settings;

/// <summary>
/// Speed tab: merges Bandwidth (rate limits) + Queue (active limits) + seeding rules from Behavior.
/// </summary>
public partial class SpeedSettingsTabViewModel : SettingsTabViewModelBase
{
    public override string TabName => "Speed";
    public override string TabIcon => "\uEE74";

    // ── Bandwidth ──

    [ObservableProperty]
    private ObservableCollection<string> _bandwidthUnitOptions = new() { "KB/s", "MB/s", "GB/s" };

    [ObservableProperty]
    private string _globalBandwidthUnit = "KB/s";

    partial void OnGlobalBandwidthUnitChanged(string value)
    {
        GlobalDownloadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(_globalDownloadLimitBytes, value);
        GlobalUploadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(_globalUploadLimitBytes, value);
    }

    [ObservableProperty]
    private string _perTorrentBandwidthUnit = "KB/s";

    partial void OnPerTorrentBandwidthUnitChanged(string value)
    {
        PerTorrentDownloadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(_perTorrentDownloadLimitBytes, value);
        PerTorrentUploadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(_perTorrentUploadLimitBytes, value);
    }

    [ObservableProperty]
    private double _globalDownloadLimitDisplay;

    [ObservableProperty]
    private double _globalUploadLimitDisplay;

    [ObservableProperty]
    private double _perTorrentDownloadLimitDisplay;

    [ObservableProperty]
    private double _perTorrentUploadLimitDisplay;

    // Internal byte-level storage for unit conversion
    private int _globalDownloadLimitBytes;
    private int _globalUploadLimitBytes;
    private int _perTorrentDownloadLimitBytes;
    private int _perTorrentUploadLimitBytes;

    // ── Queue ──

    [ObservableProperty]
    private int _maxActiveDownloads = 5;

    [ObservableProperty]
    private int _maxActiveSeeds = -1;

    [ObservableProperty]
    private int _maxActiveTorrents = 10;

    [ObservableProperty]
    private bool _dontCountSlowTorrents = true;

    // ── Seeding Rules (from Behavior) ──

    [ObservableProperty]
    private float _seedRatioLimit;

    [ObservableProperty]
    private int _seedTimeLimitMinutes;

    [ObservableProperty]
    private bool _pauseOnSeedComplete;

    [ObservableProperty]
    private bool _removeOnSeedComplete;

    public override void LoadFromSettings(GlobalSettings settings)
    {
        // Bandwidth
        _globalDownloadLimitBytes = settings.Bandwidth.GlobalDownloadLimit;
        _globalUploadLimitBytes = settings.Bandwidth.GlobalUploadLimit;
        _perTorrentDownloadLimitBytes = settings.Bandwidth.PerTorrentDownloadLimit;
        _perTorrentUploadLimitBytes = settings.Bandwidth.PerTorrentUploadLimit;

        GlobalBandwidthUnit = BandwidthUnitHelper.DetectBestUnit(
            _globalDownloadLimitBytes, _globalUploadLimitBytes);
        GlobalDownloadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(
            _globalDownloadLimitBytes, GlobalBandwidthUnit);
        GlobalUploadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(
            _globalUploadLimitBytes, GlobalBandwidthUnit);

        PerTorrentBandwidthUnit = BandwidthUnitHelper.DetectBestUnit(
            _perTorrentDownloadLimitBytes, _perTorrentUploadLimitBytes);
        PerTorrentDownloadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(
            _perTorrentDownloadLimitBytes, PerTorrentBandwidthUnit);
        PerTorrentUploadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(
            _perTorrentUploadLimitBytes, PerTorrentBandwidthUnit);

        // Queue
        MaxActiveDownloads = settings.Queue.MaxActiveDownloads;
        MaxActiveSeeds = settings.Queue.MaxActiveSeeds;
        MaxActiveTorrents = settings.Queue.MaxActiveTorrents;
        DontCountSlowTorrents = settings.Queue.DontCountSlowTorrents;

        // Seeding rules
        SeedRatioLimit = settings.Behavior.SeedRatioLimit;
        SeedTimeLimitMinutes = settings.Behavior.SeedTimeLimit;
        PauseOnSeedComplete = settings.Behavior.PauseOnSeedComplete;
        RemoveOnSeedComplete = settings.Behavior.RemoveOnSeedComplete;
    }

    public override void ApplyToSettings(GlobalSettings settings)
    {
        // Bandwidth
        settings.Bandwidth.GlobalDownloadLimit = BandwidthUnitHelper.DisplayUnitToBytes(GlobalDownloadLimitDisplay, GlobalBandwidthUnit);
        settings.Bandwidth.GlobalUploadLimit = BandwidthUnitHelper.DisplayUnitToBytes(GlobalUploadLimitDisplay, GlobalBandwidthUnit);
        settings.Bandwidth.PerTorrentDownloadLimit = BandwidthUnitHelper.DisplayUnitToBytes(PerTorrentDownloadLimitDisplay, PerTorrentBandwidthUnit);
        settings.Bandwidth.PerTorrentUploadLimit = BandwidthUnitHelper.DisplayUnitToBytes(PerTorrentUploadLimitDisplay, PerTorrentBandwidthUnit);

        // Queue
        settings.Queue.MaxActiveDownloads = MaxActiveDownloads;
        settings.Queue.MaxActiveSeeds = MaxActiveSeeds;
        settings.Queue.MaxActiveTorrents = MaxActiveTorrents;
        settings.Queue.DontCountSlowTorrents = DontCountSlowTorrents;

        // Seeding rules
        settings.Behavior.SeedRatioLimit = SeedRatioLimit;
        settings.Behavior.SeedTimeLimit = SeedTimeLimitMinutes;
        settings.Behavior.PauseOnSeedComplete = PauseOnSeedComplete;
        settings.Behavior.RemoveOnSeedComplete = RemoveOnSeedComplete;
    }
}
