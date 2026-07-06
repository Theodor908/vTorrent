using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Settings;

/// <summary>
/// Minimal torrent info needed by the scheduler for pause/resume decisions.
/// Decoupled from TorrentHandle for testability.
/// </summary>
public record SchedulerTorrentInfo(
    string InfoHash,
    TransferPhase Phase,
    UserIntent Intent,
    bool IsAutoManaged,
    bool UserPaused,
    bool IsPaused);

/// <summary>
/// Applies scheduled profile/mode changes on a 60-second tick.
/// Reads the weekly 7x24 grid from GlobalSettings.Schedule and:
///   - Profile mode: applies a named profile's settings via SettingsManager
///   - SeedOnly mode: pauses auto-managed downloading torrents
///   - Paused mode: pauses all auto-managed, non-user-paused torrents
/// Tracks which torrents it paused so it can resume them on mode transition or stop.
/// </summary>
public class ProfileScheduler : IAsyncDisposable, IDisposable
{
    private readonly SettingsManager _settingsManager;
    private readonly ProfileManager _profileManager;
    private readonly ILogger<ProfileScheduler> _logger;
    private readonly Func<IReadOnlyList<SchedulerTorrentInfo>> _getAllTorrents;
    private readonly Func<string, Task> _pauseTorrent;
    private readonly Func<string, Task> _startTorrent;
    private ITorrentService? _torrentService;

    private ScheduleCell? _lastAppliedCell;
    private readonly HashSet<string> _schedulerPausedInfoHashes = new();
    private List<ProfileSettings>? _cachedProfiles;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    /// <summary>
    /// Whether the scheduler tick loop is running.
    /// </summary>
    public bool IsRunning => _timer != null;

    public ProfileScheduler(
        SettingsManager settingsManager,
        ProfileManager profileManager,
        ILogger<ProfileScheduler> logger,
        Func<IReadOnlyList<SchedulerTorrentInfo>> getAllTorrents,
        Func<string, Task> pauseTorrent,
        Func<string, Task> startTorrent,
        ITorrentService? torrentService = null)
    {
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _getAllTorrents = getAllTorrents ?? throw new ArgumentNullException(nameof(getAllTorrents));
        _pauseTorrent = pauseTorrent ?? throw new ArgumentNullException(nameof(pauseTorrent));
        _startTorrent = startTorrent ?? throw new ArgumentNullException(nameof(startTorrent));
        _torrentService = torrentService;
    }

    /// <summary>
    /// Injects the ITorrentService after construction to avoid circular dependency.
    /// Called by TorrentService once it has been fully constructed.
    /// </summary>
    internal void InjectTorrentService(ITorrentService torrentService)
    {
        _torrentService = torrentService;
    }

    #region Public Helpers

    /// <summary>
    /// Maps .NET DayOfWeek to the schedule grid's Sunday-first index: Sunday=0 .. Saturday=6.
    /// Matches <see cref="ScheduleSettings.Grid"/>'s documented convention and the UI's
    /// (uiDayIdx + 1) % 7 storage formula.
    /// </summary>
    public static int MapDayOfWeek(DayOfWeek dow) => (int)dow;

    /// <summary>
    /// Compares two schedule cells for logical equality (mode + profile name, case-insensitive).
    /// </summary>
    public static bool CellEquals(ScheduleCell? a, ScheduleCell? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Mode == b.Mode
            && string.Equals(a.ProfileName, b.ProfileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Drives a scheduler to match the desired Enabled state. Idempotent.
    /// Used at boot (so a settings file with Schedule.Enabled=true starts the scheduler
    /// without waiting for an OnChange transition) and from the settings monitor callback.
    /// </summary>
    public static void EnsureMatchesEnabled(ProfileScheduler scheduler, bool enabled)
    {
        if (enabled && !scheduler.IsRunning)
            scheduler.Start();
        else if (!enabled && scheduler.IsRunning)
            Task.Run(() => scheduler.StopAsync());
    }

    #endregion

    #region Lifecycle

    /// <summary>
    /// Start the scheduler: force-apply the current cell, then tick every 60 seconds.
    /// </summary>
    public void Start()
    {
        if (IsRunning) return; // already started

        // Set timer BEFORE ApplyCurrentCellAsync so IsRunning == true during the apply.
        // This prevents re-entry: ApplyCurrentCellAsync → UpdateAndSaveAsync → NotifyMonitors
        // → ScheduleSettings OnChange callback → checks IsRunning → true → skips Start().
        _lastAppliedCell = null;
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        _ = ApplyCurrentCellAsync();
        _ = RunLoop(_cts.Token);
    }

    /// <summary>
    /// Stop the scheduler, resume any torrents it paused, and clean up.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }

        if (_timer != null)
        {
            _timer.Dispose();
            _timer = null;
        }

        _lastAppliedCell = null;
        await ResumeSchedulerPausedTorrentsAsync().ConfigureAwait(false);
    }

    #endregion

    #region Tick Loop

    private async Task RunLoop(CancellationToken ct)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await OnTickAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ProfileScheduler tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop — exit silently.
        }
    }

    /// <summary>
    /// Evaluate the current schedule cell and apply if changed. Exposed internally for testing.
    /// </summary>
    internal async Task OnTickAsync()
    {
        var schedule = _settingsManager.Current.Schedule;
        if (!schedule.Enabled)
            return;

        var dayIndex = MapDayOfWeek(DateTime.Now.DayOfWeek);
        var hour = DateTime.Now.Hour;

        if (dayIndex < 0 || dayIndex >= schedule.Grid.Length)
            return;
        if (hour < 0 || hour >= schedule.Grid[dayIndex].Length)
            return;

        var cell = schedule.Grid[dayIndex][hour];

        if (CellEquals(cell, _lastAppliedCell))
            return; // no change

        await ApplyCellAsync(cell).ConfigureAwait(false);
        _lastAppliedCell = cell;
    }

    #endregion

    #region Cell Application

    /// <summary>
    /// Apply a schedule cell: resume previously paused torrents, then enforce the new mode.
    /// Exposed internally for direct testing.
    /// </summary>
    internal async Task ApplyCellAsync(ScheduleCell cell)
    {
        // Non-blocking: skip if already applying. Prevents re-entry from
        // ApplyCellAsync → UpdateAndSaveAsync → NotifyMonitors → OnChange, and also
        // guards against genuine concurrent access from RunLoop / StopAsync.
        if (!await _applyLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
        _logger.LogInformation("ProfileScheduler applying cell: Mode={Mode}, Profile={Profile}",
            cell.Mode, cell.ProfileName);

        // Resume phase: un-pause anything we previously paused
        if (_schedulerPausedInfoHashes.Count > 0)
        {
            foreach (var hash in _schedulerPausedInfoHashes.ToList())
            {
                try
                {
                    await _startTorrent(hash).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resume scheduler-paused torrent {InfoHash}", hash);
                }
            }
            _schedulerPausedInfoHashes.Clear();
        }

        // Apply phase
        switch (cell.Mode)
        {
            case ScheduleCellMode.Profile:
                var profile = await GetProfileByNameAsync(cell.ProfileName).ConfigureAwait(false);
                await _settingsManager.UpdateAndSaveAsync(gs =>
                {
                    profile.Settings.ApplyTo(gs);
                    gs.ActiveProfileName = profile.Name;
                    gs.ActiveProfileColor = profile.Color;
                }).ConfigureAwait(false);
                _torrentService?.NotifyProfileChanged(profile.Name);
                break;

            case ScheduleCellMode.SeedOnly:
                var torrentsForSeedOnly = _getAllTorrents();
                foreach (var t in torrentsForSeedOnly)
                {
                    if (t.IsAutoManaged && !t.UserPaused && t.Phase == TransferPhase.Downloading)
                    {
                        try
                        {
                            await _pauseTorrent(t.InfoHash).ConfigureAwait(false);
                            _schedulerPausedInfoHashes.Add(t.InfoHash);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to pause torrent {InfoHash} for SeedOnly", t.InfoHash);
                        }
                    }
                }
                break;

            case ScheduleCellMode.Paused:
                var torrentsForPause = _getAllTorrents();
                foreach (var t in torrentsForPause)
                {
                    if (t.IsAutoManaged && !t.UserPaused && !t.IsPaused)
                    {
                        try
                        {
                            await _pauseTorrent(t.InfoHash).ConfigureAwait(false);
                            _schedulerPausedInfoHashes.Add(t.InfoHash);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to pause torrent {InfoHash} for Paused mode", t.InfoHash);
                        }
                    }
                }
                break;
        }
        }
        finally
        {
            _applyLock.Release();
        }
    }

    #endregion

    #region Helpers

    private async Task<ProfileSettings> GetProfileByNameAsync(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return ProfilePresets.Balanced;

        // Per-tick invalidation (v1 simplicity)
        _cachedProfiles = null;
        _cachedProfiles = await _profileManager.LoadAllAsync().ConfigureAwait(false);

        var match = _cachedProfiles.FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (match != null)
            return match;

        _logger.LogWarning("Scheduled profile '{ProfileName}' not found, falling back to Balanced", name);
        return ProfilePresets.Balanced;
    }

    /// <summary>
    /// Force-apply the current schedule cell regardless of change detection.
    /// </summary>
    internal async Task ApplyCurrentCellAsync()
    {
        _lastAppliedCell = null;
        await OnTickAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Resume all torrents that were paused by the scheduler.
    /// </summary>
    internal async Task ResumeSchedulerPausedTorrentsAsync()
    {
        await _applyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var hash in _schedulerPausedInfoHashes.ToList())
            {
                try
                {
                    await _startTorrent(hash).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resume scheduler-paused torrent {InfoHash}", hash);
                }
            }
            _schedulerPausedInfoHashes.Clear();
        }
        finally
        {
            _applyLock.Release();
        }
    }

    #endregion

    #region Dispose

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _applyLock.Dispose();
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _applyLock.Dispose();
    }

    #endregion
}
