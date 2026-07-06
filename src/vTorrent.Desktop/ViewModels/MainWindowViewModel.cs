using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Persistence;
using vTorrent.Core.Settings;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels;

/// <summary>
/// Main ViewModel that acts as the composition root for the application.
/// Orchestrates child ViewModels and manages application-level state.
/// Follows Dependency Inversion - depends on abstractions (INavigationService).
/// </summary>
public partial class MainWindowViewModel : BaseViewModel
{
    #region Services

    private readonly ITorrentManagerService? _torrentManager;
    private readonly SessionPersistence? _persistence;
    private readonly INavigationService _navigationService;
    private readonly IThemeService _themeService;
    private ProfileManager? _profileManager;

    #endregion

    #region Child ViewModels

    [ObservableProperty]
    private HeaderViewModel _header;

    [ObservableProperty]
    private SidebarViewModel _sidebar;

    [ObservableProperty]
    private TorrentListViewModel _torrentList;

    [ObservableProperty]
    private TransferStatsViewModel _transferStats;

    #endregion

    #region Window State

    [ObservableProperty]
    private string _title = "vTorrent";

    [ObservableProperty]
    private bool _isMaximized;

    #endregion

    #region Profile Indicator

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveProfileDisplayName))]
    private string _activeProfileName = "Balanced";

    [ObservableProperty]
    private string _activeProfileColor = "#2196F3";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveProfileDisplayName))]
    private bool _isProfileDrifted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveProfileDisplayName))]
    private bool _isScheduleActive;

    public string ActiveProfileDisplayName
    {
        get
        {
            var name = IsProfileDrifted ? $"{ActiveProfileName} (modified)" : ActiveProfileName;
            return IsScheduleActive ? $"\u23F0 {name}" : name;
        }
    }

    public ObservableCollection<ProfileSettings> AllProfiles { get; } = new();

    #endregion

    public MainWindowViewModel() : this(null, null, null)
    {
    }

    public MainWindowViewModel(ITorrentManagerService? torrentManager) : this(torrentManager, null, null)
    {
    }

    public MainWindowViewModel(ITorrentManagerService? torrentManager, SessionPersistence? persistence, ViewState? viewState)
    {
        _torrentManager = torrentManager;
        _persistence = persistence;

        // Create services
        _navigationService = new NavigationService();

        // Use the ThemeService from TorrentManager if available (shared instance)
        // This ensures Settings window and Sidebar share the same ThemeService
        if (_torrentManager?.ThemeService != null)
        {
            _themeService = _torrentManager.ThemeService;
        }
        else
        {
            // Fallback for design-time or when no torrent manager
            _themeService = new ThemeService(Application.Current!);
            _themeService.Initialize();
        }

        // Create child ViewModels with dependency injection
        _torrentList = new TorrentListViewModel(_navigationService, _torrentManager);
        _sidebar = new SidebarViewModel(_navigationService, _torrentList, _themeService, _torrentManager);
        _header = new HeaderViewModel(_themeService);
        _transferStats = new TransferStatsViewModel(_torrentManager);

        // Wire up persistence for view state
        if (_persistence != null)
        {
            _torrentList.SetPersistence(_persistence);
        }

        // Apply saved view state
        if (viewState != null)
        {
            _torrentList.ApplyViewState(viewState);

            // Restore navigation section
            if (viewState.HasValidSection() && Enum.TryParse<NavigationSection>(viewState.ActiveSection, true, out var section))
            {
                _navigationService.NavigateTo(section);
            }

            // Restore graph toggle state
            _transferStats.ShowDownloadLine = viewState.ShowDownloadLine;
            _transferStats.ShowUploadLine = viewState.ShowUploadLine;
            _torrentList.GraphShowDownloadLine = viewState.ShowDownloadLine;
            _torrentList.GraphShowUploadLine = viewState.ShowUploadLine;
        }

        // Save graph toggle state when user toggles download/upload lines
        _transferStats.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(TransferStatsViewModel.ShowDownloadLine))
            {
                _torrentList.GraphShowDownloadLine = _transferStats.ShowDownloadLine;
                _torrentList.SaveViewState();
            }
            else if (e.PropertyName == nameof(TransferStatsViewModel.ShowUploadLine))
            {
                _torrentList.GraphShowUploadLine = _transferStats.ShowUploadLine;
                _torrentList.SaveViewState();
            }
        };

        // Initialize theme state for transfer stats chart
        _transferStats.IsDarkTheme = _themeService.IsDarkTheme;

        // Subscribe to theme changes to update transfer stats chart
        _themeService.ThemeChanged += (s, theme) =>
        {
            _transferStats.IsDarkTheme = theme == ThemeMode.Dark;
        };

        // TransferStatsViewModel handles its own StatsUpdated subscription when torrent manager is available.
        // For sample data mode, wire up to torrent list changes.
        if (_torrentManager == null)
        {
            // No torrent manager - use sample data
            // Wire up transfer stats to torrent list for sample data mode
            _torrentList.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(TorrentListViewModel.FilteredTorrents))
                {
                    _transferStats.UpdateFromGrid(_torrentList.FilteredTorrents.ToList());
                }
            };

            // Initial stats update from sample data
            _transferStats.UpdateFromGrid(_torrentList.FilteredTorrents.ToList());
        }
    }

    #region Window Commands

    [RelayCommand]
    private void Minimize()
    {
        // Handled by view
    }

    [RelayCommand]
    private void ToggleMaximize()
    {
        IsMaximized = !IsMaximized;
    }

    [RelayCommand]
    private void Close()
    {
        // Handled by view
    }

    #endregion

    #region Profile Commands

    /// <summary>
    /// Set the ProfileManager (called from code-behind after DI is available).
    /// </summary>
    public void SetProfileManager(ProfileManager profileManager)
    {
        _profileManager = profileManager;
    }

    /// <summary>
    /// Load profile state: active profile name/color, all profiles, and compute drift.
    /// </summary>
    public async Task LoadProfileStateAsync()
    {
        try
        {
            var settingsManager = _torrentManager?.SettingsManager;
            if (settingsManager == null)
            {
                // No settings manager — use defaults
                return;
            }

            var gs = settingsManager.Current;
            ActiveProfileName = gs.ActiveProfileName;
            ActiveProfileColor = gs.ActiveProfileColor;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsScheduleActive = gs.Schedule.Enabled;
            });

            if (_profileManager != null)
            {
                var profiles = await _profileManager.LoadAllAsync().ConfigureAwait(false);

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    AllProfiles.Clear();
                    foreach (var p in profiles)
                        AllProfiles.Add(p);
                });

                // Compute drift
                var active = profiles.FirstOrDefault(
                    p => string.Equals(p.Name, ActiveProfileName, StringComparison.OrdinalIgnoreCase));
                if (active != null)
                {
                    var currentSnapshot = ProfileSettingsValues.SnapshotFrom(gs);
                    var drifted = !currentSnapshot.ValueEquals(active.Settings);

                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        IsProfileDrifted = drifted;
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load profile state: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SwitchProfileAsync(string? profileName)
    {
        if (string.IsNullOrEmpty(profileName)) return;

        var settingsManager = _torrentManager?.SettingsManager;
        if (settingsManager == null) return;

        var target = AllProfiles.FirstOrDefault(
            p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (target == null) return;

        try
        {
            await settingsManager.UpdateAndSaveAsync(gs =>
            {
                target.Settings.ApplyTo(gs);
                gs.ActiveProfileName = target.Name;
                gs.ActiveProfileColor = target.Color;
            }).ConfigureAwait(false);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ActiveProfileName = target.Name;
                ActiveProfileColor = target.Color;
                IsProfileDrifted = false;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to switch profile: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveDriftToProfileAsync()
    {
        if (_profileManager == null) return;

        var settingsManager = _torrentManager?.SettingsManager;
        if (settingsManager == null) return;

        try
        {
            var gs = settingsManager.Current;
            var snapshot = ProfileSettingsValues.SnapshotFrom(gs);

            var profiles = await _profileManager.LoadAllAsync().ConfigureAwait(false);
            var active = profiles.FirstOrDefault(
                p => string.Equals(p.Name, ActiveProfileName, StringComparison.OrdinalIgnoreCase));
            if (active == null) return;

            active.Settings = snapshot;
            await _profileManager.SaveAsync(active).ConfigureAwait(false);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsProfileDrifted = false;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save drift to profile: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RevertToProfileAsync()
    {
        if (_profileManager == null) return;

        var settingsManager = _torrentManager?.SettingsManager;
        if (settingsManager == null) return;

        try
        {
            var profiles = await _profileManager.LoadAllAsync().ConfigureAwait(false);
            var active = profiles.FirstOrDefault(
                p => string.Equals(p.Name, ActiveProfileName, StringComparison.OrdinalIgnoreCase));
            if (active == null) return;

            await settingsManager.UpdateAndSaveAsync(gs =>
            {
                active.Settings.ApplyTo(gs);
            }).ConfigureAwait(false);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsProfileDrifted = false;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to revert to profile: {ex.Message}");
        }
    }

    #endregion
}
