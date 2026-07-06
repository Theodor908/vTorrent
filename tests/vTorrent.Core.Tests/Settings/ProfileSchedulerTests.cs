using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Settings;
using Xunit;

namespace vTorrent.Core.Tests.Settings;

public class ProfileSchedulerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SettingsManager _settingsManager;
    private readonly ProfileManager _profileManager;
    private readonly Mock<ILogger<ProfileScheduler>> _loggerMock;

    // Tracking collections for delegate calls
    private readonly List<string> _pausedHashes = new();
    private readonly List<string> _startedHashes = new();
    private List<SchedulerTorrentInfo> _fakeTorrents = new();

    public ProfileSchedulerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "vtorrent_scheduler_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var smLogger = new Mock<ILogger<SettingsManager>>();
        _settingsManager = new SettingsManager(_tempDir, smLogger.Object);
        _profileManager = new ProfileManager(_tempDir);
        _loggerMock = new Mock<ILogger<ProfileScheduler>>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private ProfileScheduler CreateScheduler()
    {
        return new ProfileScheduler(
            _settingsManager,
            _profileManager,
            _loggerMock.Object,
            () => _fakeTorrents,
            hash => { _pausedHashes.Add(hash); return Task.CompletedTask; },
            hash => { _startedHashes.Add(hash); return Task.CompletedTask; });
    }

    private void EnableSchedule()
    {
        _settingsManager.Update(gs => gs.Schedule.Enabled = true);
    }

    #region MapDayOfWeek Tests

    // ScheduleSettings.Grid docstring: "day 0 = Sunday".
    // The UI (ProfilesSettingsTabViewModel) stores cells at Grid[(uiDayIdx + 1) % 7][hour]
    // where UI dayIdx 0 = Monday, so storage day 0 ends up being Sunday.
    // The scheduler MUST agree with that convention or it reads the wrong cell.
    [Theory]
    [InlineData(DayOfWeek.Sunday, 0)]
    [InlineData(DayOfWeek.Monday, 1)]
    [InlineData(DayOfWeek.Tuesday, 2)]
    [InlineData(DayOfWeek.Wednesday, 3)]
    [InlineData(DayOfWeek.Thursday, 4)]
    [InlineData(DayOfWeek.Friday, 5)]
    [InlineData(DayOfWeek.Saturday, 6)]
    public void MapDayOfWeek_SundayIs0_SaturdayIs6(DayOfWeek dow, int expected)
    {
        ProfileScheduler.MapDayOfWeek(dow).Should().Be(expected);
    }

    // Round-trip lock: for each UI day index, the scheduler's calendar-day lookup
    // MUST resolve to the same grid slot the UI writes into. If this drifts, every
    // painted cell gets applied on the wrong day.
    [Theory]
    [InlineData(0, DayOfWeek.Monday)]
    [InlineData(1, DayOfWeek.Tuesday)]
    [InlineData(2, DayOfWeek.Wednesday)]
    [InlineData(3, DayOfWeek.Thursday)]
    [InlineData(4, DayOfWeek.Friday)]
    [InlineData(5, DayOfWeek.Saturday)]
    [InlineData(6, DayOfWeek.Sunday)]
    public void MapDayOfWeek_AgreesWithUiGridStorageMapping(int uiDayIdx, DayOfWeek dow)
    {
        // UI write site (ProfilesSettingsTabViewModel.ApplyScheduleToSettings):
        int uiStorageIndex = (uiDayIdx + 1) % 7;
        // Scheduler read site (ProfileScheduler.OnTickAsync):
        int schedulerIndex = ProfileScheduler.MapDayOfWeek(dow);
        schedulerIndex.Should().Be(uiStorageIndex,
            "the UI and scheduler must read/write the same grid slot for the same calendar day");
    }

    #endregion

    #region CellEquals Tests

    [Fact]
    public void CellEquals_SameModeAndProfile_ReturnsTrue()
    {
        var a = new ScheduleCell { Mode = ScheduleCellMode.Profile, ProfileName = "Balanced" };
        var b = new ScheduleCell { Mode = ScheduleCellMode.Profile, ProfileName = "Balanced" };
        ProfileScheduler.CellEquals(a, b).Should().BeTrue();
    }

    [Fact]
    public void CellEquals_DifferentMode_ReturnsFalse()
    {
        var a = new ScheduleCell { Mode = ScheduleCellMode.Profile, ProfileName = "Balanced" };
        var b = new ScheduleCell { Mode = ScheduleCellMode.Paused, ProfileName = "Balanced" };
        ProfileScheduler.CellEquals(a, b).Should().BeFalse();
    }

    [Fact]
    public void CellEquals_DifferentProfile_ReturnsFalse()
    {
        var a = new ScheduleCell { Mode = ScheduleCellMode.Profile, ProfileName = "Quiet" };
        var b = new ScheduleCell { Mode = ScheduleCellMode.Profile, ProfileName = "Performance" };
        ProfileScheduler.CellEquals(a, b).Should().BeFalse();
    }

    [Fact]
    public void CellEquals_CaseInsensitive()
    {
        var a = new ScheduleCell { Mode = ScheduleCellMode.Profile, ProfileName = "quiet" };
        var b = new ScheduleCell { Mode = ScheduleCellMode.Profile, ProfileName = "Quiet" };
        ProfileScheduler.CellEquals(a, b).Should().BeTrue();
    }

    [Fact]
    public void CellEquals_BothNull_ReturnsTrue()
    {
        ProfileScheduler.CellEquals(null, null).Should().BeTrue();
    }

    [Fact]
    public void CellEquals_OneNull_ReturnsFalse()
    {
        var a = new ScheduleCell { Mode = ScheduleCellMode.Profile, ProfileName = "Balanced" };
        ProfileScheduler.CellEquals(a, null).Should().BeFalse();
        ProfileScheduler.CellEquals(null, a).Should().BeFalse();
    }

    #endregion

    #region EnsureMatchesEnabled — boot/runtime symmetry

    // The orchestrator must use the same logic at app startup AND on settings-change events.
    // Without a boot-time call, a settings file with Schedule.Enabled=true on disk
    // would never start the scheduler — OnChange only fires on actual transitions.
    [Fact]
    public async Task EnsureMatchesEnabled_True_StartsStoppedScheduler()
    {
        var s = CreateScheduler();
        s.IsRunning.Should().BeFalse();

        ProfileScheduler.EnsureMatchesEnabled(s, enabled: true);

        s.IsRunning.Should().BeTrue();
        await s.StopAsync();
    }

    [Fact]
    public void EnsureMatchesEnabled_False_LeavesStoppedSchedulerStopped()
    {
        var s = CreateScheduler();

        ProfileScheduler.EnsureMatchesEnabled(s, enabled: false);

        s.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureMatchesEnabled_True_NoOpIfAlreadyRunning()
    {
        var s = CreateScheduler();
        s.Start();
        s.IsRunning.Should().BeTrue();

        ProfileScheduler.EnsureMatchesEnabled(s, enabled: true);

        // Still running, no exception.
        s.IsRunning.Should().BeTrue();
        await s.StopAsync();
    }

    #endregion

    #region ApplyCellAsync Integration Tests

    [Fact]
    public async Task ApplyCell_ProfileMode_CallsUpdateAndSave()
    {
        var scheduler = CreateScheduler();
        var cell = new ScheduleCell { Mode = ScheduleCellMode.Profile, ProfileName = "Quiet" };

        await scheduler.ApplyCellAsync(cell);

        // After applying "Quiet" profile, settings should reflect Quiet's values
        _settingsManager.Current.ActiveProfileName.Should().Be("Quiet");
        _settingsManager.Current.ActiveProfileColor.Should().Be("#78909C");
        // Spot-check a Quiet-specific value
        _settingsManager.Current.Bandwidth.GlobalDownloadLimit.Should().Be(1 * 1024 * 1024);
    }

    [Fact]
    public async Task ApplyCell_PausedMode_PausesAutoManagedOnly()
    {
        _fakeTorrents = new List<SchedulerTorrentInfo>
        {
            new("hash1", TransferPhase.Downloading, UserIntent.Active, IsAutoManaged: true, UserPaused: false, IsPaused: false),
            new("hash2", TransferPhase.Seeding, UserIntent.Active, IsAutoManaged: true, UserPaused: false, IsPaused: false),
            new("hash3", TransferPhase.Downloading, UserIntent.Active, IsAutoManaged: false, UserPaused: false, IsPaused: false), // forced
        };

        var scheduler = CreateScheduler();
        var cell = new ScheduleCell { Mode = ScheduleCellMode.Paused };

        await scheduler.ApplyCellAsync(cell);

        // Auto-managed, non-paused: hash1 (downloading) and hash2 (seeding) should be paused
        _pausedHashes.Should().Contain("hash1");
        _pausedHashes.Should().Contain("hash2");
        // Forced torrent should NOT be paused
        _pausedHashes.Should().NotContain("hash3");
    }

    [Fact]
    public async Task ApplyCell_PausedMode_SkipsForcedTorrents()
    {
        _fakeTorrents = new List<SchedulerTorrentInfo>
        {
            new("forced1", TransferPhase.Downloading, UserIntent.Active, IsAutoManaged: false, UserPaused: false, IsPaused: false),
            new("forced2", TransferPhase.Seeding, UserIntent.Active, IsAutoManaged: false, UserPaused: false, IsPaused: false),
        };

        var scheduler = CreateScheduler();
        var cell = new ScheduleCell { Mode = ScheduleCellMode.Paused };

        await scheduler.ApplyCellAsync(cell);

        _pausedHashes.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyCell_SeedOnlyMode_PausesOnlyDownloaders()
    {
        _fakeTorrents = new List<SchedulerTorrentInfo>
        {
            new("dl1", TransferPhase.Downloading, UserIntent.Active, IsAutoManaged: true, UserPaused: false, IsPaused: false),
            new("seed1", TransferPhase.Seeding, UserIntent.Active, IsAutoManaged: true, UserPaused: false, IsPaused: false),
            new("dl2", TransferPhase.Downloading, UserIntent.Active, IsAutoManaged: true, UserPaused: false, IsPaused: false),
        };

        var scheduler = CreateScheduler();
        var cell = new ScheduleCell { Mode = ScheduleCellMode.SeedOnly };

        await scheduler.ApplyCellAsync(cell);

        _pausedHashes.Should().BeEquivalentTo(new[] { "dl1", "dl2" });
        _pausedHashes.Should().NotContain("seed1");
    }

    [Fact]
    public async Task ApplyCell_TransitionPausedToProfile_ResumesSchedulerPaused()
    {
        _fakeTorrents = new List<SchedulerTorrentInfo>
        {
            new("hash1", TransferPhase.Downloading, UserIntent.Active, IsAutoManaged: true, UserPaused: false, IsPaused: false),
            new("hash2", TransferPhase.Seeding, UserIntent.Active, IsAutoManaged: true, UserPaused: false, IsPaused: false),
        };

        var scheduler = CreateScheduler();

        // First: apply Paused mode (pauses both torrents)
        await scheduler.ApplyCellAsync(new ScheduleCell { Mode = ScheduleCellMode.Paused });
        _pausedHashes.Should().HaveCount(2);

        // Clear tracking
        _startedHashes.Clear();

        // Now transition to Profile mode — should resume the 2 scheduler-paused torrents
        await scheduler.ApplyCellAsync(new ScheduleCell { Mode = ScheduleCellMode.Profile, ProfileName = "Balanced" });

        _startedHashes.Should().BeEquivalentTo(new[] { "hash1", "hash2" });
    }

    [Fact]
    public async Task ApplyCell_TransitionPausedToProfile_DoesNotResumeUserPaused()
    {
        _fakeTorrents = new List<SchedulerTorrentInfo>
        {
            new("auto1", TransferPhase.Downloading, UserIntent.Active, IsAutoManaged: true, UserPaused: false, IsPaused: false),
            new("userPaused1", TransferPhase.Idle, UserIntent.Paused, IsAutoManaged: true, UserPaused: true, IsPaused: true),
        };

        var scheduler = CreateScheduler();

        // Apply Paused mode — only auto1 gets scheduler-paused (userPaused1 is already UserPaused)
        await scheduler.ApplyCellAsync(new ScheduleCell { Mode = ScheduleCellMode.Paused });
        _pausedHashes.Should().ContainSingle().Which.Should().Be("auto1");

        _startedHashes.Clear();

        // Transition to Profile — only auto1 should be resumed
        await scheduler.ApplyCellAsync(new ScheduleCell { Mode = ScheduleCellMode.Profile, ProfileName = "Balanced" });

        _startedHashes.Should().ContainSingle().Which.Should().Be("auto1");
    }

    [Fact]
    public async Task Stop_ResumesAllSchedulerPaused()
    {
        _fakeTorrents = new List<SchedulerTorrentInfo>
        {
            new("hash1", TransferPhase.Downloading, UserIntent.Active, IsAutoManaged: true, UserPaused: false, IsPaused: false),
            new("hash2", TransferPhase.Seeding, UserIntent.Active, IsAutoManaged: true, UserPaused: false, IsPaused: false),
        };

        var scheduler = CreateScheduler();

        // Pause both via scheduler
        await scheduler.ApplyCellAsync(new ScheduleCell { Mode = ScheduleCellMode.Paused });
        _pausedHashes.Should().HaveCount(2);

        _startedHashes.Clear();

        // Stop the scheduler — should resume both
        await scheduler.StopAsync();

        _startedHashes.Should().BeEquivalentTo(new[] { "hash1", "hash2" });
    }

    #endregion
}
