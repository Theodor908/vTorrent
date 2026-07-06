using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using vTorrent.Core.Settings;
using vTorrent.Desktop.Services;
using vTorrent.Desktop.ViewModels.Settings;

namespace vTorrent.Desktop.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the standalone Global Speed Limits dialog (tray menu shortcut).
/// </summary>
public partial class SpeedLimitsDialogViewModel : ObservableObject
{
    private readonly SettingsManager? _settingsManager;
    private readonly ITorrentManagerService? _torrentManager;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnlimitedDownload))]
    private double _downloadLimitDisplay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnlimitedUpload))]
    private double _uploadLimitDisplay;

    [ObservableProperty]
    private string _selectedUnit = "KB/s";

    [ObservableProperty]
    private ObservableCollection<string> _unitOptions = new() { "KB/s", "MB/s", "GB/s" };

    public bool IsUnlimitedDownload => DownloadLimitDisplay == 0;
    public bool IsUnlimitedUpload => UploadLimitDisplay == 0;

    public bool DialogResult { get; set; }

    /// <summary>
    /// Design-time / test constructor.
    /// </summary>
    public SpeedLimitsDialogViewModel()
    {
    }

    /// <summary>
    /// Runtime constructor with service dependencies.
    /// </summary>
    public SpeedLimitsDialogViewModel(SettingsManager settingsManager, ITorrentManagerService torrentManager)
    {
        _settingsManager = settingsManager;
        _torrentManager = torrentManager;

        var settings = settingsManager.Current;
        LoadFromBytes(settings.Bandwidth.GlobalDownloadLimit, settings.Bandwidth.GlobalUploadLimit);
    }

    /// <summary>
    /// Load display values from raw bytes/s, auto-detecting the best unit.
    /// </summary>
    public void LoadFromBytes(int downloadBytes, int uploadBytes)
    {
        var unit = BandwidthUnitHelper.DetectBestUnit(downloadBytes, uploadBytes);
        SelectedUnit = unit;
        DownloadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(downloadBytes, unit);
        UploadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(uploadBytes, unit);
    }

    public int GetDownloadLimitBytes() =>
        BandwidthUnitHelper.DisplayUnitToBytes(DownloadLimitDisplay, SelectedUnit);

    public int GetUploadLimitBytes() =>
        BandwidthUnitHelper.DisplayUnitToBytes(UploadLimitDisplay, SelectedUnit);

    /// <summary>
    /// Apply the limits to settings and save.
    /// </summary>
    public async Task ApplyAsync()
    {
        if (_settingsManager == null) return;

        var settings = _settingsManager.Current;
        settings.Bandwidth.GlobalDownloadLimit = GetDownloadLimitBytes();
        settings.Bandwidth.GlobalUploadLimit = GetUploadLimitBytes();

        await _settingsManager.SaveAsync();

        if (_torrentManager != null)
            await _torrentManager.Service.ApplySettingsAsync();
    }
}
