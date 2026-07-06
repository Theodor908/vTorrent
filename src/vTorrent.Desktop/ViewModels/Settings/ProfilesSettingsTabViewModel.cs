using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Settings;

// Schedule grid constants
// ScheduleSettings.Grid: day 0 = Sunday, UI DayIndex: 0 = Monday
// Mapping: UI dayIndex -> grid day = (dayIndex + 1) % 7

namespace vTorrent.Desktop.ViewModels.Settings;

public partial class ProfilesSettingsTabViewModel : SettingsTabViewModelBase, IDisposable
{
    private readonly ProfileManager _profileManager;
    private readonly SettingsManager _settingsManager;
    private ScheduleExporter? _scheduleExporter;

    // Debounced schedule flush: PaintCell schedules persistence directly inside the VM
    // so it does not depend on the View successfully wiring pointer-released handlers
    // (FindControl-at-DataContextChanged is a known race when the schedule grid lives
    // behind an IsVisible-toggled panel).
    private CancellationTokenSource? _pendingScheduleFlushCts;
    private const int ScheduleFlushDebounceMs = 300;

    public void SetScheduleExporter(ScheduleExporter exporter)
    {
        _scheduleExporter = exporter;
    }

    public override string TabName => "Profiles";
    public override string TabIcon => "\uE0F8";

    // ── Observable Properties ──

    public ObservableCollection<ProfileSettings> Profiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedProfileActive))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    private ProfileSettings? _selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedProfileActive))]
    private string _activeProfileName = "Balanced";

    [ObservableProperty]
    private string _profileStatusMessage = "";

    // ── Schedule Properties ──

    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private ScheduleCellMode _paintMode = ScheduleCellMode.Profile;
    [ObservableProperty] private string _paintProfileName = "Balanced";
    [ObservableProperty] private PaintOption? _selectedPaintOption;

    public ObservableCollection<ScheduleCellViewModel> GridCells { get; } = new();
    public ObservableCollection<PaintOption> PaintOptions { get; } = new();

    public static string[] DayLabels { get; } = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
    public static string[] HourLabels { get; } = Enumerable.Range(0, 24).Select(h => h.ToString("D2")).ToArray();

    // ── Computed Properties ──

    public bool IsSelectedProfileActive =>
        SelectedProfile != null &&
        string.Equals(SelectedProfile.Name, ActiveProfileName, StringComparison.OrdinalIgnoreCase);

    public bool CanDeleteSelected =>
        SelectedProfile != null && !ProfilePresets.IsBuiltIn(SelectedProfile.Name);

    // ── Constructor ──

    public ProfilesSettingsTabViewModel() : this(null!, null!) { }

    public ProfilesSettingsTabViewModel(ProfileManager profileManager, SettingsManager settingsManager)
    {
        _profileManager = profileManager;
        _settingsManager = settingsManager;
    }

    // ── Core Event Subscription ──

    public void SubscribeToCoreEvents(ITorrentService torrentService)
    {
        torrentService.ProfileChanged += OnProfileChanged;
        torrentService.ScheduleToggled += OnScheduleToggled;
    }

    private void OnProfileChanged(object? sender, string profileName)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ActiveProfileName = profileName;
        });
    }

    private void OnScheduleToggled(object? sender, bool enabled)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ScheduleEnabled = enabled;
        });
    }

    // ── Settings Tab Overrides ──

    public override void LoadFromSettings(GlobalSettings settings)
    {
        ActiveProfileName = settings.ActiveProfileName;
        _ = LoadProfilesAsync();
        LoadScheduleFromSettings(settings);
    }

    public override void ApplyToSettings(GlobalSettings settings)
    {
        // Profiles manage their own persistence via ProfileManager.
        // Schedule settings are written here.
        ApplyScheduleToSettings(settings);
    }

    // ── Profile Loading ──

    public async Task LoadProfilesAsync()
    {
        try
        {
            var all = await _profileManager.LoadAllAsync().ConfigureAwait(false);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Profiles.Clear();
                foreach (var p in all)
                    Profiles.Add(p);

                // Re-select the active profile if possible
                SelectedProfile = Profiles.FirstOrDefault(
                    p => string.Equals(p.Name, ActiveProfileName, StringComparison.OrdinalIgnoreCase));

                RebuildPaintOptions();

                // Profiles just became available. The schedule grid was loaded
                // synchronously before this completed, so every Profile-mode cell
                // got the fallback color (Profiles was empty during ResolveProfileColor).
                // Re-resolve now that we know the real colors.
                RefreshGridCellColors();
            });
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Failed to load profiles: {ex.Message}";
        }
    }

    /// <summary>
    /// Re-applies the resolved color to every cell from its current Mode/ProfileName.
    /// Called after the Profiles collection becomes available so that schedule cells
    /// loaded before profiles finished loading get their correct fill color.
    /// </summary>
    public void RefreshGridCellColors()
    {
        foreach (var vm in GridCells)
        {
            var resolved = ResolveProfileColor(vm.ProfileName);
            vm.CellColor = vm.Mode switch
            {
                ScheduleCellMode.Profile => resolved,
                ScheduleCellMode.SeedOnly => "#FFC107",
                ScheduleCellMode.Paused => "#3C3C3C",
                _ => "#2196F3"
            };
        }
    }

    // ── Commands ──

    /// <summary>
    /// Save a new profile (created via the Save As Profile dialog) and refresh the list.
    /// </summary>
    public async Task SaveNewProfileAsync(ProfileSettings profile)
    {
        try
        {
            await _profileManager.SaveAsync(profile).ConfigureAwait(false);
            ProfileStatusMessage = $"Created profile: {profile.Name}";
            await LoadProfilesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Failed to save profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ActivateProfileAsync()
    {
        if (SelectedProfile == null) return;

        try
        {
            var profile = SelectedProfile;
            await _settingsManager.UpdateAndSaveAsync(gs =>
            {
                profile.Settings.ApplyTo(gs);
                gs.ActiveProfileName = profile.Name;
                gs.ActiveProfileColor = profile.Color;
            }).ConfigureAwait(false);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ActiveProfileName = profile.Name;
                ProfileStatusMessage = $"Activated profile: {profile.Name}";
            });
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Failed to activate profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile == null || ProfilePresets.IsBuiltIn(SelectedProfile.Name))
            return;

        try
        {
            var name = SelectedProfile.Name;
            await _profileManager.DeleteAsync(name).ConfigureAwait(false);
            ProfileStatusMessage = $"Deleted profile: {name}";
            await LoadProfilesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Failed to delete profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DuplicateProfileAsync()
    {
        if (SelectedProfile == null) return;

        try
        {
            var source = SelectedProfile;
            var newProfile = new ProfileSettings
            {
                Name = $"Copy of {source.Name}",
                Color = source.Color,
                Scope = source.Scope,
                Settings = new ProfileSettingsValues
                {
                    // Bandwidth
                    GlobalDownloadLimit = source.Settings.GlobalDownloadLimit,
                    GlobalUploadLimit = source.Settings.GlobalUploadLimit,
                    PerTorrentDownloadLimit = source.Settings.PerTorrentDownloadLimit,
                    PerTorrentUploadLimit = source.Settings.PerTorrentUploadLimit,
                    MixedModeAlgorithm = source.Settings.MixedModeAlgorithm,
                    // Connection
                    MaxGlobalConnections = source.Settings.MaxGlobalConnections,
                    MaxConnectionsPerTorrent = source.Settings.MaxConnectionsPerTorrent,
                    MaxUploadsPerTorrent = source.Settings.MaxUploadsPerTorrent,
                    MaxHalfOpenConnections = source.Settings.MaxHalfOpenConnections,
                    ConnectionSpeed = source.Settings.ConnectionSpeed,
                    // Queue
                    MaxActiveDownloads = source.Settings.MaxActiveDownloads,
                    MaxActiveSeeds = source.Settings.MaxActiveSeeds,
                    MaxActiveTorrents = source.Settings.MaxActiveTorrents,
                    DontCountSlowTorrents = source.Settings.DontCountSlowTorrents,
                    ConnectSeedEveryNDownload = source.Settings.ConnectSeedEveryNDownload,
                    // Choking
                    ChokingAlgorithm = source.Settings.ChokingAlgorithm,
                    SeedChokingAlgorithm = source.Settings.SeedChokingAlgorithm,
                    UnchokeSlots = source.Settings.UnchokeSlots,
                    UnchokeInterval = source.Settings.UnchokeInterval,
                    OptimisticUnchokeInterval = source.Settings.OptimisticUnchokeInterval,
                    NumOptimisticUnchokeSlots = source.Settings.NumOptimisticUnchokeSlots,
                    // Peer
                    PeerTurnover = source.Settings.PeerTurnover,
                    PeerTurnoverCutoff = source.Settings.PeerTurnoverCutoff,
                    PeerTurnoverInterval = source.Settings.PeerTurnoverInterval,
                    MaxPendingBlocksPerPeer = source.Settings.MaxPendingBlocksPerPeer,
                    // Disk
                    BackendType = source.Settings.BackendType,
                    CacheSize = source.Settings.CacheSize,
                    MaxOutstandingDiskRequests = source.Settings.MaxOutstandingDiskRequests,
                    HashThreads = source.Settings.HashThreads,
                    // Seeding
                    SeedRatioLimit = source.Settings.SeedRatioLimit,
                    SeedTimeLimit = source.Settings.SeedTimeLimit,
                    PauseOnSeedComplete = source.Settings.PauseOnSeedComplete,
                    RemoveOnSeedComplete = source.Settings.RemoveOnSeedComplete,
                    // Picker
                    InitialPickerThreshold = source.Settings.InitialPickerThreshold,
                    WholePiecesThreshold = source.Settings.WholePiecesThreshold
                }
            };

            await _profileManager.SaveAsync(newProfile).ConfigureAwait(false);
            ProfileStatusMessage = $"Created: {newProfile.Name}";
            await LoadProfilesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Failed to duplicate profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportProfileAsync(string filePath)
    {
        if (SelectedProfile == null || string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            await _profileManager.ExportAsync(SelectedProfile, filePath).ConfigureAwait(false);
            ProfileStatusMessage = $"Exported: {SelectedProfile.Name}";
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Failed to export profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportProfileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            var result = await _profileManager.ImportAsync(filePath).ConfigureAwait(false);

            if (result.Profile == null)
            {
                ProfileStatusMessage = $"Import failed: {string.Join("; ", result.Warnings)}";
                return;
            }

            if (result.Warnings.Count > 0)
            {
                ProfileStatusMessage = $"Imported with warnings: {string.Join("; ", result.Warnings)}";
            }
            else
            {
                ProfileStatusMessage = $"Imported: {result.Profile.Name}";
            }

            if (result.HasNameConflict)
            {
                result.Profile.Name = $"{result.Profile.Name} (imported)";
            }

            await _profileManager.SaveAsync(result.Profile).ConfigureAwait(false);
            await LoadProfilesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Failed to import profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportScheduleAsync(string filePath)
    {
        if (_scheduleExporter == null || string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            await _scheduleExporter.ExportAsync(filePath).ConfigureAwait(false);
            ProfileStatusMessage = "Schedule exported successfully.";
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Failed to export schedule: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportScheduleAsync(string filePath)
    {
        if (_scheduleExporter == null || string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            var result = await _scheduleExporter.ImportAsync(filePath).ConfigureAwait(false);
            if (!result.Success)
            {
                ProfileStatusMessage = $"Import failed: {string.Join("; ", result.Warnings)}";
                return;
            }

            var parts = new List<string>();
            if (result.ImportedProfiles.Count > 0)
                parts.Add($"Imported: {string.Join(", ", result.ImportedProfiles)}");
            if (result.RenamedProfiles.Count > 0)
                parts.Add($"Renamed: {string.Join(", ", result.RenamedProfiles.Select(kv => $"{kv.Key} \u2192 {kv.Value}"))}");
            if (result.SkippedProfiles.Count > 0)
                parts.Add($"Skipped: {string.Join(", ", result.SkippedProfiles)}");

            ProfileStatusMessage = parts.Count > 0 ? string.Join(". ", parts) : "Schedule imported.";

            await LoadProfilesAsync().ConfigureAwait(false);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LoadScheduleFromSettings(_settingsManager.Current);
            });
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Failed to import schedule: {ex.Message}";
        }
    }

    // ── Schedule Methods ──

    /// <summary>
    /// Populates the 168 GridCells from schedule settings.
    /// UI DayIndex 0=Mon..6=Sun maps to Grid day=(dayIndex+1)%7 (Grid day 0=Sun).
    /// </summary>
    public void LoadScheduleFromSettings(GlobalSettings settings)
    {
        ScheduleEnabled = settings.Schedule.Enabled;

        GridCells.Clear();
        for (int dayIdx = 0; dayIdx < 7; dayIdx++)
        {
            int gridDay = (dayIdx + 1) % 7; // Mon=1, Tue=2, ... Sun=0
            for (int hour = 0; hour < 24; hour++)
            {
                var cell = settings.Schedule.Grid[gridDay][hour];
                var vm = new ScheduleCellViewModel { DayIndex = dayIdx, HourIndex = hour };
                var color = ResolveProfileColor(cell.ProfileName);
                vm.SetFromCell(cell, color);
                GridCells.Add(vm);
            }
        }
    }

    /// <summary>
    /// Writes schedule state back to GlobalSettings.
    /// </summary>
    public void ApplyScheduleToSettings(GlobalSettings settings)
    {
        settings.Schedule.Enabled = ScheduleEnabled;

        foreach (var vm in GridCells)
        {
            int gridDay = (vm.DayIndex + 1) % 7;
            settings.Schedule.Grid[gridDay][vm.HourIndex] = vm.ToCell();
        }
    }

    /// <summary>
    /// Persists the current schedule grid to settings + disk immediately.
    /// Cancels any pending debounced flush so the explicit call wins.
    /// PaintCell already schedules a debounced flush, so most call sites do not
    /// need to invoke this — it is exposed for snappy "flush now" hooks
    /// (pointer-released, OnClosed, bulk paint) and for tests.
    /// </summary>
    public Task FlushScheduleAsync()
    {
        _pendingScheduleFlushCts?.Cancel();
        return _settingsManager.UpdateAndSaveAsync(ApplyScheduleToSettings);
    }

    private void ScheduleDebouncedFlush()
    {
        _pendingScheduleFlushCts?.Cancel();
        var cts = new CancellationTokenSource();
        _pendingScheduleFlushCts = cts;
        _ = RunDebouncedFlushAsync(cts.Token);
    }

    private async Task RunDebouncedFlushAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ScheduleFlushDebounceMs, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            await _settingsManager.UpdateAndSaveAsync(ApplyScheduleToSettings).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* superseded by a newer paint or explicit flush */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Debounced schedule flush failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _pendingScheduleFlushCts?.Cancel();
        _pendingScheduleFlushCts?.Dispose();
        _pendingScheduleFlushCts = null;
    }

    /// <summary>
    /// Paints a single cell at the given index with the current PaintMode/PaintProfileName.
    /// Schedules a debounced persistence flush so the paint survives a Settings-window close
    /// even when the View's pointer-released wiring has not attached.
    /// </summary>
    public void PaintCell(int cellIndex)
    {
        if (cellIndex < 0 || cellIndex >= GridCells.Count) return;

        var vm = GridCells[cellIndex];
        var cell = new ScheduleCell
        {
            Mode = PaintMode,
            ProfileName = PaintMode == ScheduleCellMode.Profile ? PaintProfileName : null
        };
        var color = ResolveProfileColor(PaintProfileName);
        vm.SetFromCell(cell, color);

        ScheduleDebouncedFlush();
    }

    /// <summary>
    /// Paints a range of cells (inclusive).
    /// </summary>
    public void PaintCellRange(int startIndex, int endIndex)
    {
        int lo = Math.Min(startIndex, endIndex);
        int hi = Math.Max(startIndex, endIndex);
        for (int i = lo; i <= hi; i++)
            PaintCell(i);
    }

    /// <summary>Paint all 24 hours for a specific day (0=Mon..6=Sun).</summary>
    public void PaintDay(int dayIndex)
    {
        int start = dayIndex * 24;
        PaintCellRange(start, start + 23);
    }

    /// <summary>Paint all 168 cells (all days, all hours).</summary>
    public void PaintAllDays()
    {
        PaintCellRange(0, 167);
    }

    /// <summary>
    /// Rebuilds the PaintOptions dropdown from current profiles.
    /// Called after profiles are loaded.
    /// </summary>
    public void RebuildPaintOptions()
    {
        var previousSelection = SelectedPaintOption;
        PaintOptions.Clear();

        // Add all profiles
        foreach (var profile in Profiles)
        {
            PaintOptions.Add(new PaintOption
            {
                DisplayName = profile.Name,
                Color = profile.Color,
                Mode = ScheduleCellMode.Profile,
                ProfileName = profile.Name,
                IsSeparator = false
            });
        }

        // Separator
        PaintOptions.Add(new PaintOption { IsSeparator = true, DisplayName = "─────────" });

        // Special modes
        PaintOptions.Add(new PaintOption
        {
            DisplayName = "Seed Only",
            Color = "#FFC107",
            Mode = ScheduleCellMode.SeedOnly,
            IsSeparator = false
        });

        PaintOptions.Add(new PaintOption
        {
            DisplayName = "Paused",
            Color = "#3C3C3C",
            Mode = ScheduleCellMode.Paused,
            IsSeparator = false
        });

        // Restore or default selection
        if (previousSelection != null && !previousSelection.IsSeparator)
        {
            var match = PaintOptions.FirstOrDefault(o =>
                !o.IsSeparator && o.Mode == previousSelection.Mode &&
                string.Equals(o.ProfileName, previousSelection.ProfileName, StringComparison.OrdinalIgnoreCase));
            SelectedPaintOption = match ?? PaintOptions.FirstOrDefault(o => !o.IsSeparator);
        }
        else
        {
            SelectedPaintOption = PaintOptions.FirstOrDefault(o => !o.IsSeparator);
        }
    }

    /// <summary>
    /// Applies the selected paint option to PaintMode/PaintProfileName.
    /// Called from code-behind or property change handler.
    /// </summary>
    partial void OnSelectedPaintOptionChanged(PaintOption? value)
    {
        if (value == null || value.IsSeparator) return;
        PaintMode = value.Mode;
        PaintProfileName = value.ProfileName ?? "Balanced";
    }

    private string ResolveProfileColor(string? profileName)
    {
        if (string.IsNullOrEmpty(profileName)) return "#2196F3";

        var profile = Profiles.FirstOrDefault(
            p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));

        return profile?.Color ?? "#2196F3";
    }
}

/// <summary>
/// Represents a paint mode option in the schedule grid dropdown.
/// Combines profiles and special modes (Seed Only, Paused) with a separator.
/// </summary>
public class PaintOption
{
    public string DisplayName { get; init; } = "";
    public string? Color { get; init; }
    public ScheduleCellMode Mode { get; init; } = ScheduleCellMode.Profile;
    public string? ProfileName { get; init; }
    public bool IsSeparator { get; init; }
}
