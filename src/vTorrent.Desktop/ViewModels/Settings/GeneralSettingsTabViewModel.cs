using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Desktop.ViewModels.Settings;

/// <summary>
/// General tab: merges Appearance (theme, notifications) + Storage (paths, disk) settings.
/// </summary>
public partial class GeneralSettingsTabViewModel : SettingsTabViewModelBase
{
    public override string TabName => "General";
    public override string TabIcon => "\uE270";

    // ── Appearance ──

    [ObservableProperty]
    private ObservableCollection<string> _themeOptions = new() { "Dark", "Light", "System" };

    [ObservableProperty]
    private string _selectedTheme = "Dark";

    [ObservableProperty]
    private bool _notificationsEnabled = true;

    [ObservableProperty]
    private bool _notifyOnDownloadComplete = true;

    [ObservableProperty]
    private bool _notifyOnDownloadFailed = true;

    [ObservableProperty]
    private bool _notifyOnTorrentAdded;

    [ObservableProperty]
    private bool _playNotificationSound = true;

    // ── System Tray ──

    [ObservableProperty]
    private bool _closeToTray = true;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _startMinimizedToTray;

    // ── Storage ──

    [ObservableProperty]
    private string _defaultSavePath = string.Empty;

    [ObservableProperty]
    private bool _useIncompleteSavePath;

    [ObservableProperty]
    private string _incompleteSavePath = "";

    [ObservableProperty]
    private bool _preallocateFiles;

    [ObservableProperty]
    private int _writeBufferSizeMb = 64;

    public override void LoadFromSettings(GlobalSettings settings)
    {
        // Appearance
        SelectedTheme = settings.UI.Theme;
        NotificationsEnabled = settings.UI.NotificationsEnabled;
        NotifyOnDownloadComplete = settings.UI.NotifyOnDownloadComplete;
        NotifyOnDownloadFailed = settings.UI.NotifyOnDownloadFailed;
        NotifyOnTorrentAdded = settings.UI.NotifyOnTorrentAdded;
        PlayNotificationSound = settings.UI.PlayNotificationSound;

        // System Tray
        CloseToTray = settings.UI.CloseToTray;
        MinimizeToTray = settings.UI.MinimizeToTray;
        StartMinimizedToTray = settings.UI.StartMinimizedToTray;

        // Storage
        DefaultSavePath = settings.Disk.DefaultSavePath;
        IncompleteSavePath = settings.Disk.IncompleteSavePath;
        UseIncompleteSavePath = !string.IsNullOrEmpty(settings.Disk.IncompleteSavePath);
        PreallocateFiles = settings.Disk.PreallocateFiles;
        WriteBufferSizeMb = (int)(settings.Disk.CacheSize / (1024 * 1024));
    }

    public override void ApplyToSettings(GlobalSettings settings)
    {
        // Appearance
        settings.UI.Theme = SelectedTheme;
        settings.UI.NotificationsEnabled = NotificationsEnabled;
        settings.UI.NotifyOnDownloadComplete = NotifyOnDownloadComplete;
        settings.UI.NotifyOnDownloadFailed = NotifyOnDownloadFailed;
        settings.UI.NotifyOnTorrentAdded = NotifyOnTorrentAdded;
        settings.UI.PlayNotificationSound = PlayNotificationSound;

        // System Tray
        settings.UI.CloseToTray = CloseToTray;
        settings.UI.MinimizeToTray = MinimizeToTray;
        settings.UI.StartMinimizedToTray = StartMinimizedToTray;

        // Storage
        settings.Disk.DefaultSavePath = DefaultSavePath;
        settings.Disk.IncompleteSavePath = UseIncompleteSavePath ? IncompleteSavePath : "";
        settings.Disk.PreallocateFiles = PreallocateFiles;
        settings.Disk.CacheSize = WriteBufferSizeMb * 1024L * 1024L;
    }

    /// <summary>
    /// Set the default save path (called from view after folder selection).
    /// </summary>
    public void SetDefaultSavePath(string path)
    {
        if (!string.IsNullOrEmpty(path))
            DefaultSavePath = path;
    }

    /// <summary>
    /// Set the incomplete save path (called from view after folder selection).
    /// </summary>
    public void SetIncompleteSavePath(string path)
    {
        IncompleteSavePath = path;
        UseIncompleteSavePath = !string.IsNullOrEmpty(path);
    }
}
