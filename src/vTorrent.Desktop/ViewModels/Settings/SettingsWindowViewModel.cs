using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Settings;
using vTorrent.Core;
using vTorrent.Core.Settings;
using vTorrent.Core.Utilities;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels.Settings;

/// <summary>
/// Coordinator ViewModel for the Settings Window.
/// Delegates property storage and load/apply logic to tab ViewModels.
/// </summary>
public partial class SettingsWindowViewModel : ObservableObject
{
    private readonly SettingsManager? _settingsManager;
    private readonly ITorrentManagerService? _torrentManager;
    private readonly IThemeService? _themeService;
    private GlobalSettings? _settings;
    private PropagatableFieldTracker? _tracker;

    #region Tab ViewModels

    public GeneralSettingsTabViewModel GeneralTab { get; } = new();
    public ConnectionSettingsTabViewModel ConnectionTab { get; } = new();
    public SpeedSettingsTabViewModel SpeedTab { get; } = new();
    public BitTorrentSettingsTabViewModel BitTorrentTab { get; } = new();
    public ServerSettingsTabViewModel ServerTab { get; }
    public AdvancedSettingsTabViewModel AdvancedTab { get; } = new();
    public ProfilesSettingsTabViewModel ProfilesTab { get; }

    public ObservableCollection<SettingsTabViewModelBase> Tabs { get; }

    #endregion

    #region Observable Properties

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _hasDirtyBandwidth;

    [ObservableProperty]
    private bool _hasDirtyConnection;

    [ObservableProperty]
    private bool _hasDirtySeedingRules;

    #endregion

    #region Events

    /// <summary>
    /// Raised when the dialog should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Raised when browse for default save path is requested.
    /// </summary>
    public event EventHandler? BrowseDefaultSavePathRequested;

    /// <summary>
    /// Raised when browse for incomplete save path is requested.
    /// </summary>
    public event EventHandler? BrowseIncompleteSavePathRequested;

    /// <summary>
    /// Raised when browse for log file path is requested.
    /// </summary>
    public event EventHandler? BrowseLogFilePathRequested;

    /// <summary>
    /// Raised when the user wants to save current settings as a profile.
    /// </summary>
    public event EventHandler? SaveAsProfileRequested;

    #endregion

    #region Constructor

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public SettingsWindowViewModel() : this(null, null, null) { }

    /// <summary>
    /// Runtime constructor.
    /// </summary>
    public SettingsWindowViewModel(
        SettingsManager? settingsManager,
        ITorrentManagerService? torrentManager,
        IThemeService? themeService = null,
        ServerHostService? serverHost = null,
        WebUIBundleScanner? bundleScanner = null,
        string? bundlesDirectory = null,
        ProfileManager? profileManager = null)
    {
        _settingsManager = settingsManager;
        _torrentManager = torrentManager;
        _themeService = themeService;

        ServerTab = new ServerSettingsTabViewModel(serverHost, bundleScanner, bundlesDirectory ?? "");

        var resolvedProfileManager = profileManager ?? new ProfileManager(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vTorrent"));

        ProfilesTab = new ProfilesSettingsTabViewModel(resolvedProfileManager, settingsManager!);

        var scheduleExporter = new ScheduleExporter(resolvedProfileManager, settingsManager!);
        ProfilesTab.SetScheduleExporter(scheduleExporter);

        if (torrentManager != null)
        {
            ProfilesTab.SubscribeToCoreEvents(torrentManager.Service);
        }

        Tabs = new ObservableCollection<SettingsTabViewModelBase>
        {
            GeneralTab,      // 0
            ConnectionTab,   // 1
            SpeedTab,        // 2
            BitTorrentTab,   // 3
            ServerTab,       // 4
            AdvancedTab,     // 5
            ProfilesTab      // 6
        };

        // Wire up property-changed on each tab to auto-save
        foreach (var tab in Tabs)
        {
            tab.PropertyChanged += OnTabPropertyChanged;
        }

        // Build tracker with getter lambdas from _propagatableSettings
        var getters = new Dictionary<string, Func<GlobalSettings, object>>();
        foreach (var (_, info) in _propagatableSettings)
        {
            getters[info.SettingName] = info.GetValue;
        }
        _tracker = new PropagatableFieldTracker(getters);
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Initialize the settings view model by loading current settings.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_settingsManager == null) return;

        IsLoading = true;

        try
        {
            _settings = _settingsManager.Current;
            LoadSettingsToUI();
            StatusMessage = "Settings loaded";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load settings: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }

        await Task.CompletedTask;
    }

    private void LoadSettingsToUI()
    {
        if (_settings == null) return;

        // Temporarily suppress auto-save during bulk load
        _suppressAutoSave = true;

        foreach (var tab in Tabs)
        {
            tab.LoadFromSettings(_settings);
        }

        _suppressAutoSave = false;

        // Snapshot originals for propagatable settings tracking
        _tracker?.SnapshotOriginals(_settings);
        HasDirtyBandwidth = false;
        HasDirtyConnection = false;
        HasDirtySeedingRules = false;
    }

    #endregion

    #region Auto-Save on Property Change

    private bool _suppressAutoSave;

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressAutoSave || _settings == null || e.PropertyName == null) return;

        // Check if this is a propagatable setting
        if (sender != null && _propagatableSettings.TryGetValue((sender.GetType(), e.PropertyName), out var info))
        {
            // Propagatable: update in-memory settings for unit conversion, track dirty state, but do NOT save
            if (sender is SettingsTabViewModelBase tab)
            {
                tab.ApplyToSettings(_settings);
            }
            var currentValue = info.GetValue(_settings);
            _tracker?.OnPropertyChanged(info.SettingName, currentValue);
            UpdateDirtyFlags();
            return; // Do not auto-save — user must click Save button
        }

        // Non-propagatable: auto-save immediately (unchanged behavior)
        if (sender is SettingsTabViewModelBase nonPropTab)
        {
            nonPropTab.ApplyToSettings(_settings);
        }
        SaveSettingsAsync().FireAndForget();
        _ = ApplyRuntimeSettingsAsync();

        // Special handling for specific property changes
        if (sender is GeneralSettingsTabViewModel && e.PropertyName == nameof(GeneralSettingsTabViewModel.SelectedTheme))
        {
            ApplyTheme(GeneralTab.SelectedTheme);
        }

        if (sender is BitTorrentSettingsTabViewModel && e.PropertyName == nameof(BitTorrentSettingsTabViewModel.EnableDht))
        {
            _ = ApplyDhtSettingAsync(BitTorrentTab.EnableDht);
        }

        if (sender is BitTorrentSettingsTabViewModel && e.PropertyName == nameof(BitTorrentSettingsTabViewModel.EnableDhtDosBlocker))
        {
            _ = ApplyDhtDosBlockerSettingAsync(BitTorrentTab.EnableDhtDosBlocker);
        }

        if (sender is ConnectionSettingsTabViewModel && e.PropertyName == nameof(ConnectionSettingsTabViewModel.ListenPort))
        {
            ConnectionTab.PortChangeRequiresRestart = true;
        }
    }

    #endregion

    #region Commands

    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SelectTab(string? tabIndexString)
    {
        if (int.TryParse(tabIndexString, out int tabIndex))
        {
            SelectedTabIndex = tabIndex;
        }
    }

    [RelayCommand]
    private void BrowseDefaultSavePath()
    {
        BrowseDefaultSavePathRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void BrowseIncompleteSavePath()
    {
        BrowseIncompleteSavePathRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void BrowseLogFilePath()
    {
        BrowseLogFilePathRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SaveAsProfile()
    {
        SaveAsProfileRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task SavePerTorrentBandwidthAsync()
    {
        await SavePropagatableGroupAsync(
            PropagatableFieldTracker.SettingsGroup.PerTorrentBandwidth,
            SpeedTab);
    }

    [RelayCommand]
    private async Task SavePerTorrentConnectionAsync()
    {
        await SavePropagatableGroupAsync(
            PropagatableFieldTracker.SettingsGroup.PerTorrentConnection,
            ConnectionTab);
    }

    [RelayCommand]
    private async Task SaveSeedingRulesAsync()
    {
        await SavePropagatableGroupAsync(
            PropagatableFieldTracker.SettingsGroup.SeedingRules,
            SpeedTab);
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        if (_settingsManager == null) return;

        IsLoading = true;

        try
        {
            _settings = new GlobalSettings();
            var downloadsPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _settings.Disk.DefaultSavePath = System.IO.Path.Combine(downloadsPath, "Downloads");

            await _settingsManager.UpdateAndSaveAsync(s =>
            {
                s.Connection = _settings.Connection;
                s.Bandwidth = _settings.Bandwidth;
                s.Protocol = _settings.Protocol;
                s.Dht = _settings.Dht;
                s.Disk = _settings.Disk;
                s.Queue = _settings.Queue;
                s.Behavior = _settings.Behavior;
                s.Tracker = _settings.Tracker;
                s.Peer = _settings.Peer;
                s.AutoSave = _settings.AutoSave;
                s.Logging = _settings.Logging;
                s.Encryption = _settings.Encryption;
            });

            _settings = _settingsManager.Current;
            LoadSettingsToUI();
            StatusMessage = "Settings reset to defaults";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to reset settings: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    #endregion

    #region Public Helpers (called from View code-behind)

    /// <summary>
    /// Set the default save path (called from view after folder selection).
    /// </summary>
    public void SetDefaultSavePath(string path) => GeneralTab.SetDefaultSavePath(path);

    /// <summary>
    /// Set the incomplete save path (called from view after folder selection).
    /// </summary>
    public void SetIncompleteSavePath(string path) => GeneralTab.SetIncompleteSavePath(path);

    /// <summary>
    /// Set the log file path (called from view after folder selection).
    /// </summary>
    public void SetLogFilePath(string path) => AdvancedTab.SetLogFilePath(path);

    #endregion

    #region Private Helpers

    /// <summary>
    /// Raised when a global setting with per-torrent overrides changes.
    /// The View handles this by showing SettingsChangeDialog.
    /// </summary>
    public event Func<string, string, string, string, Task<SettingsPropagationMode>>? PropagationRequested;

    /// <summary>
    /// Maps (TabType, PropertyName) to (SettingName for propagation, display name, value getter from GlobalSettings).
    /// </summary>
    private static readonly Dictionary<(Type, string), (string SettingName, string DisplayName, Func<GlobalSettings, object> GetValue)> _propagatableSettings = new()
    {
        // Connection settings
        { (typeof(ConnectionSettingsTabViewModel), nameof(ConnectionSettingsTabViewModel.MaxConnectionsPerTorrent)),
          ("MaxConnectionsPerTorrent", "Max Connections Per Torrent", s => s.Connection.MaxConnectionsPerTorrent) },
        { (typeof(ConnectionSettingsTabViewModel), nameof(ConnectionSettingsTabViewModel.MaxUploadsPerTorrent)),
          ("MaxUploadsPerTorrent", "Max Uploads Per Torrent", s => s.Connection.MaxUploadsPerTorrent) },

        // Bandwidth settings — property names are *Display (the UI-bound property that fires PropertyChanged)
        // but the GetValue lambda reads the raw bytes/s from GlobalSettings (set by ApplyToSettings)
        { (typeof(SpeedSettingsTabViewModel), nameof(SpeedSettingsTabViewModel.PerTorrentUploadLimitDisplay)),
          ("PerTorrentUploadLimit", "Per-Torrent Upload Limit", s => s.Bandwidth.PerTorrentUploadLimit) },
        { (typeof(SpeedSettingsTabViewModel), nameof(SpeedSettingsTabViewModel.PerTorrentDownloadLimitDisplay)),
          ("PerTorrentDownloadLimit", "Per-Torrent Download Limit", s => s.Bandwidth.PerTorrentDownloadLimit) },

        // Seeding/Behavior settings (now on SpeedTab)
        { (typeof(SpeedSettingsTabViewModel), nameof(SpeedSettingsTabViewModel.SeedRatioLimit)),
          ("SeedRatioLimit", "Seed Ratio Limit", s => (object)s.Behavior.SeedRatioLimit) },
        { (typeof(SpeedSettingsTabViewModel), nameof(SpeedSettingsTabViewModel.SeedTimeLimitMinutes)),
          ("SeedTimeLimit", "Seed Time Limit", s => (object)s.Behavior.SeedTimeLimit) },
        { (typeof(SpeedSettingsTabViewModel), nameof(SpeedSettingsTabViewModel.RemoveOnSeedComplete)),
          ("RemoveOnSeedComplete", "Remove On Seed Complete", s => (object)s.Behavior.RemoveOnSeedComplete) },
        { (typeof(SpeedSettingsTabViewModel), nameof(SpeedSettingsTabViewModel.PauseOnSeedComplete)),
          ("PauseOnSeedComplete", "Pause On Seed Complete", s => (object)s.Behavior.PauseOnSeedComplete) },
    };

    /// <summary>
    /// Saves a group of propagatable settings and shows propagation dialog for each changed setting.
    /// Called by the per-section Save buttons.
    /// </summary>
    private async Task SavePropagatableGroupAsync(PropagatableFieldTracker.SettingsGroup group, SettingsTabViewModelBase tab)
    {
        if (_tracker == null || _settings == null || _settingsManager == null) return;

        var changes = _tracker.GetChangesForGroup(group);
        if (changes.Count == 0) return;

        try
        {
            // Apply tab values to settings (includes unit conversion for bandwidth).
            // Defensive/idempotent — OnTabPropertyChanged already calls this on each change.
            tab.ApplyToSettings(_settings);

            await SaveSettingsAsync();
            await ApplyRuntimeSettingsAsync();

            // Show propagation dialog for each changed setting
            if (PropagationRequested != null)
            {
                foreach (var (settingName, original, current) in changes)
                {
                    // Look up display name
                    var displayName = settingName;
                    foreach (var (_, propInfo) in _propagatableSettings)
                    {
                        if (propInfo.SettingName == settingName)
                        {
                            displayName = propInfo.DisplayName;
                            break;
                        }
                    }

                    var mode = await PropagationRequested.Invoke(
                        settingName, displayName,
                        original?.ToString() ?? "0", current?.ToString() ?? "0");

                    if (mode != SettingsPropagationMode.None && original != null)
                    {
                        await _settingsManager.PropagateGlobalSettingAsync(settingName, original, mode);
                    }
                }

                // Apply propagated overrides to engine
                await ApplyRuntimeSettingsAsync();
            }

            // Reset tracker for this group — re-snapshot originals from current settings
            _tracker.ResetGroup(group, _settings);
            UpdateDirtyFlags();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save settings: {ex.Message}";
            // Keep dirty — don't reset tracker so button stays enabled for retry
        }
    }

    /// <summary>
    /// Updates the HasDirty* properties from the tracker state.
    /// </summary>
    private void UpdateDirtyFlags()
    {
        if (_tracker == null) return;
        HasDirtyBandwidth = _tracker.HasChangesInGroup(PropagatableFieldTracker.SettingsGroup.PerTorrentBandwidth);
        HasDirtyConnection = _tracker.HasChangesInGroup(PropagatableFieldTracker.SettingsGroup.PerTorrentConnection);
        HasDirtySeedingRules = _tracker.HasChangesInGroup(PropagatableFieldTracker.SettingsGroup.SeedingRules);
    }

    private async Task SaveSettingsAsync()
    {
        if (_settingsManager == null || _settings == null) return;

        try
        {
            await _settingsManager.SaveAsync();
            StatusMessage = "Settings saved";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save: {ex.Message}";
        }
    }

    private async Task ApplyRuntimeSettingsAsync()
    {
        if (_torrentManager == null) return;

        try
        {
            await _torrentManager.Service.ApplySettingsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to apply settings: {ex.Message}";
        }
    }

    private async Task ApplyDhtSettingAsync(bool enabled)
    {
        if (_torrentManager == null) return;

        try
        {
            if (enabled)
                await _torrentManager.EnableDhtAsync();
            else
                await _torrentManager.DisableDhtAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to toggle DHT: {ex.Message}";
        }
    }

    private async Task ApplyDhtDosBlockerSettingAsync(bool enabled)
    {
        if (_torrentManager == null) return;

        try
        {
            await _torrentManager.Service.ApplySettingsAsync();
            StatusMessage = enabled ? "DHT DoS protection enabled" : "DHT DoS protection disabled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to apply DHT setting: {ex.Message}";
        }
    }

    private void ApplyTheme(string themeName)
    {
        if (_themeService == null) return;

        try
        {
            var mode = themeName switch
            {
                "Light" => ThemeMode.Light,
                "System" => ThemeMode.System,
                _ => ThemeMode.Dark
            };

            _themeService.SetTheme(mode);
            StatusMessage = $"Theme changed to {themeName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to apply theme: {ex.Message}";
        }
    }

    #endregion

    #region PropagatableFieldTracker

    /// <summary>
    /// Tracks dirty state for propagatable settings. Compares current values against
    /// originals snapshotted at load time. UI-thread-only, no locking needed.
    /// </summary>
    internal sealed class PropagatableFieldTracker
    {
        public enum SettingsGroup { PerTorrentBandwidth, PerTorrentConnection, SeedingRules }

        // settingName -> group
        private static readonly Dictionary<string, SettingsGroup> _groupMap = new()
        {
            ["PerTorrentUploadLimit"] = SettingsGroup.PerTorrentBandwidth,
            ["PerTorrentDownloadLimit"] = SettingsGroup.PerTorrentBandwidth,
            ["MaxConnectionsPerTorrent"] = SettingsGroup.PerTorrentConnection,
            ["MaxUploadsPerTorrent"] = SettingsGroup.PerTorrentConnection,
            ["SeedRatioLimit"] = SettingsGroup.SeedingRules,
            ["SeedTimeLimit"] = SettingsGroup.SeedingRules,
            ["RemoveOnSeedComplete"] = SettingsGroup.SeedingRules,
            ["PauseOnSeedComplete"] = SettingsGroup.SeedingRules,
        };

        // settingName -> getter lambda (GlobalSettings -> value)
        private readonly Dictionary<string, Func<GlobalSettings, object>> _getters;

        // State
        private readonly Dictionary<string, object?> _originals = new();
        private readonly Dictionary<string, object?> _current = new();

        public PropagatableFieldTracker(Dictionary<string, Func<GlobalSettings, object>> getters)
        {
            _getters = getters;
        }

        public void SnapshotOriginals(GlobalSettings settings)
        {
            _originals.Clear();
            _current.Clear();
            foreach (var (name, getter) in _getters)
            {
                var value = getter(settings);
                _originals[name] = value;
                _current[name] = value;
            }
        }

        public void OnPropertyChanged(string settingName, object? currentValue)
        {
            if (_current.ContainsKey(settingName))
            {
                _current[settingName] = currentValue;
            }
        }

        public bool HasChanges => _originals.Any(kvp =>
            !Equals(kvp.Value, _current.GetValueOrDefault(kvp.Key)));

        public bool HasChangesInGroup(SettingsGroup group)
        {
            return _groupMap
                .Where(kvp => kvp.Value == group)
                .Any(kvp => _originals.TryGetValue(kvp.Key, out var orig)
                            && !Equals(orig, _current.GetValueOrDefault(kvp.Key)));
        }

        public IReadOnlyList<(string settingName, object? original, object? current)> GetChangesForGroup(SettingsGroup group)
        {
            var result = new List<(string, object?, object?)>();
            foreach (var (settingName, settingsGroup) in _groupMap)
            {
                if (settingsGroup != group) continue;
                if (!_originals.TryGetValue(settingName, out var orig)) continue;
                var cur = _current.GetValueOrDefault(settingName);
                if (!Equals(orig, cur))
                {
                    result.Add((settingName, orig, cur));
                }
            }
            return result;
        }

        public void ResetGroup(SettingsGroup group, GlobalSettings currentSettings)
        {
            foreach (var (settingName, settingsGroup) in _groupMap)
            {
                if (settingsGroup != group) continue;
                if (_getters.TryGetValue(settingName, out var getter))
                {
                    var value = getter(currentSettings);
                    _originals[settingName] = value;
                    _current[settingName] = value;
                }
            }
        }
    }

    /// <summary>
    /// Factory method for testing. Creates a tracker with the standard getter lambdas.
    /// </summary>
    internal static PropagatableFieldTracker CreateTrackerForTesting()
    {
        var getters = new Dictionary<string, Func<GlobalSettings, object>>();
        foreach (var (_, info) in _propagatableSettings)
        {
            getters[info.SettingName] = info.GetValue;
        }
        return new PropagatableFieldTracker(getters);
    }

    #endregion
}

/// <summary>
/// Shared bandwidth unit conversion utilities.
/// </summary>
internal static class BandwidthUnitHelper
{
    public static double BytesToDisplayUnit(int bytes, string unit) => unit switch
    {
        "MB/s" => bytes / (1024.0 * 1024.0),
        "GB/s" => bytes / (1024.0 * 1024.0 * 1024.0),
        _ => bytes / 1024.0 // KB/s
    };

    public static int DisplayUnitToBytes(double displayValue, string unit)
    {
        var bytes = unit switch
        {
            "MB/s" => displayValue * 1024.0 * 1024.0,
            "GB/s" => displayValue * 1024.0 * 1024.0 * 1024.0,
            _ => displayValue * 1024.0 // KB/s
        };
        return (int)Math.Round(bytes);
    }

    public static string DetectBestUnit(int bytes1, int bytes2)
    {
        var maxBytes = Math.Max(bytes1, bytes2);
        if (maxBytes >= 1024 * 1024 * 1024) return "GB/s";
        if (maxBytes >= 1024 * 1024) return "MB/s";
        return "KB/s";
    }
}
