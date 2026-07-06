using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Settings;
using vTorrent.Desktop.ViewModels.Settings;
using Xunit;

namespace vTorrent.Tests.Unit.ViewModels;

/// <summary>
/// Locks the painting → persistence path. Painting only mutates child cell VMs,
/// which do NOT propagate PropertyChanged to the auto-save listener on
/// SettingsWindowViewModel. FlushScheduleAsync is the explicit persistence hook.
/// </summary>
public class ProfilesSettingsTabViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SettingsManager _settingsManager;
    private readonly ProfileManager _profileManager;

    public ProfilesSettingsTabViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "vtorrent_profilestab_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsManager = new SettingsManager(_tempDir, NullLogger<SettingsManager>.Instance);
        _profileManager = new ProfileManager(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private ProfilesSettingsTabViewModel CreateVm()
    {
        var vm = new ProfilesSettingsTabViewModel(_profileManager, _settingsManager);
        vm.LoadFromSettings(_settingsManager.Current);
        return vm;
    }

    [Fact]
    public async Task FlushScheduleAsync_PersistsPaintedCellToSettingsManager()
    {
        var vm = CreateVm();
        vm.PaintMode = ScheduleCellMode.Profile;
        vm.PaintProfileName = "Quiet";

        // UI dayIdx 1 = Tuesday, hour 14. UI storage formula: (1+1)%7 = grid[2][14].
        const int uiTuesday = 1;
        const int hour = 14;
        int cellIndex = uiTuesday * 24 + hour;
        vm.PaintCell(cellIndex);

        // Sanity: in-memory VM has it (the bug is that this is ALL that happens today).
        vm.GridCells[cellIndex].ProfileName.Should().Be("Quiet");

        // Before flush: SettingsManager still has default Balanced.
        _settingsManager.Current.Schedule.Grid[2][hour].ProfileName
            .Should().Be("Balanced", "no flush yet, persistence must not happen by side-effect");

        await vm.FlushScheduleAsync();

        // After flush: persistence reflects the paint.
        _settingsManager.Current.Schedule.Grid[2][hour].ProfileName.Should().Be("Quiet");
        _settingsManager.Current.Schedule.Grid[2][hour].Mode.Should().Be(ScheduleCellMode.Profile);
    }

    [Fact]
    public async Task FlushScheduleAsync_PersistsScheduleEnabledTogether()
    {
        var vm = CreateVm();
        vm.ScheduleEnabled = true;
        vm.PaintMode = ScheduleCellMode.Paused;
        vm.PaintCell(0); // UI Mon 00:00 → grid[1][0]

        await vm.FlushScheduleAsync();

        _settingsManager.Current.Schedule.Enabled.Should().BeTrue();
        _settingsManager.Current.Schedule.Grid[1][0].Mode.Should().Be(ScheduleCellMode.Paused);
    }

    [Fact]
    public async Task FlushScheduleAsync_WritesToDisk()
    {
        var vm = CreateVm();
        vm.PaintMode = ScheduleCellMode.SeedOnly;
        vm.PaintCell(3 * 24 + 22); // UI Thu 22:00 → grid[4][22]

        await vm.FlushScheduleAsync();

        // Re-load a fresh SettingsManager from the same dir to verify on-disk state.
        var reloaded = new SettingsManager(_tempDir, NullLogger<SettingsManager>.Instance);
        await reloaded.LoadAsync();
        reloaded.Current.Schedule.Grid[4][22].Mode.Should().Be(ScheduleCellMode.SeedOnly);
    }

    /// <summary>
    /// LoadScheduleFromSettings runs synchronously from LoadFromSettings, but
    /// LoadProfilesAsync is fire-and-forget. So when the schedule grid is built,
    /// the Profiles collection is empty and ResolveProfileColor falls back to the
    /// default blue for every Profile-mode cell. Once profiles finish loading we
    /// must re-resolve those colors — otherwise reopened settings show every
    /// painted cell as default Balanced regardless of what's on disk.
    /// </summary>
    [Fact]
    public void RefreshGridCellColors_AppliesProfileColorsFromCurrentProfilesCollection()
    {
        var vm = CreateVm();

        // Simulate the bug condition: a Performance-painted cell that was loaded
        // before profiles became available, so its CellColor is the fallback blue.
        vm.PaintMode = ScheduleCellMode.Profile;
        vm.PaintProfileName = "Performance";
        const int sundayHour0 = 6 * 24 + 0;
        vm.PaintCell(sundayHour0);
        vm.GridCells[sundayHour0].CellColor.Should().Be("#2196F3", "Profiles is still empty here");

        // Profiles became available later (analogous to LoadProfilesAsync's UI dispatch).
        vm.Profiles.Add(new ProfileSettings { Name = "Performance", Color = "#F44336" });
        vm.RefreshGridCellColors();

        vm.GridCells[sundayHour0].CellColor.Should().Be("#F44336");
        vm.GridCells[sundayHour0].ProfileName.Should().Be("Performance");
    }

    [Fact]
    public void RefreshGridCellColors_LeavesNonProfileModesUnchanged()
    {
        var vm = CreateVm();
        vm.Profiles.Add(new ProfileSettings { Name = "Performance", Color = "#F44336" });

        vm.PaintMode = ScheduleCellMode.SeedOnly;
        vm.PaintCell(0);
        vm.PaintMode = ScheduleCellMode.Paused;
        vm.PaintCell(1);

        vm.RefreshGridCellColors();

        vm.GridCells[0].CellColor.Should().Be("#FFC107", "SeedOnly mode is amber regardless of Profiles");
        vm.GridCells[1].CellColor.Should().Be("#3C3C3C", "Paused mode is dark gray regardless of Profiles");
    }

    /// <summary>
    /// Persistence MUST NOT depend on the View successfully wiring pointer-released
    /// handlers. PaintCell itself triggers a debounced flush; after the debounce
    /// window elapses the on-disk state must reflect the paint without any explicit
    /// FlushScheduleAsync call from the caller.
    /// </summary>
    [Fact]
    public async Task PaintCell_AutoPersistsAfterDebounceWindow_WithoutExplicitFlush()
    {
        var vm = CreateVm();
        vm.PaintMode = ScheduleCellMode.Profile;
        vm.PaintProfileName = "Performance";

        // UI dayIdx 6 = Sunday, hour 0. UI storage formula: (6+1)%7 = grid[0][0].
        const int uiSunday = 6;
        const int hour = 0;
        vm.PaintCell(uiSunday * 24 + hour);

        // Wait past the debounce window (default 300 ms; pad to 800 ms for CI/scheduler jitter).
        await Task.Delay(800);

        var reloaded = new SettingsManager(_tempDir, NullLogger<SettingsManager>.Instance);
        await reloaded.LoadAsync();
        reloaded.Current.Schedule.Grid[0][hour].Mode.Should().Be(ScheduleCellMode.Profile);
        reloaded.Current.Schedule.Grid[0][hour].ProfileName.Should().Be("Performance");
    }
}
