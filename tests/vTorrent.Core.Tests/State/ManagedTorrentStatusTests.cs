using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Core.Orchestration;
using vTorrent.Core.State;
using Xunit;

namespace vTorrent.Tests.Unit.Core.State;

public class ManagedTorrentStatusTests
{
    [Fact]
    public async Task NewTorrent_Status_IsAutoManaged_DefaultsTrue()
    {
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        await mt.StateController.DrainAsync();
        Assert.True(mt.GetStatus().IsAutoManaged);
    }

    [Fact]
    public async Task NewTorrent_Property_IsAutoManaged_DefaultsTrue()
    {
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        await mt.StateController.DrainAsync();
        Assert.True(mt.IsAutoManaged);
    }

    [Fact]
    public async Task SetIsAutoManaged_False_UpdatesStatusSnapshot()
    {
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        mt.IsAutoManaged = false;
        await mt.StateController.DrainAsync();
        Assert.False(mt.GetStatus().IsAutoManaged);
    }

    [Fact]
    public async Task SetIsAutoManaged_True_UpdatesStatusSnapshot()
    {
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        mt.IsAutoManaged = false;
        await mt.StateController.DrainAsync();
        mt.IsAutoManaged = true;
        await mt.StateController.DrainAsync();
        Assert.True(mt.GetStatus().IsAutoManaged);
    }

    [Fact]
    public async Task SetIsAutoManaged_FiresStatusChangedEvent()
    {
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        StatusChangedEventArgs? captured = null;
        mt.StatusChanged += (_, args) => captured = args;

        mt.IsAutoManaged = false;
        await mt.StateController.DrainAsync();

        Assert.NotNull(captured);
        Assert.True(captured!.OldStatus.IsAutoManaged);
        Assert.False(captured.NewStatus.IsAutoManaged);
    }

    [Fact]
    public async Task SetIsAutoManaged_SameValue_NoEvent()
    {
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        int eventCount = 0;
        mt.StatusChanged += (_, _) => eventCount++;

        mt.IsAutoManaged = true;
        await mt.StateController.DrainAsync();

        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task NewTorrent_InitialStatus_IsIdle()
    {
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        await mt.StateController.DrainAsync();
        var status = mt.GetStatus();
        Assert.Equal(TransferPhase.Idle, status.Phase);
        Assert.Equal(UserIntent.Paused, status.Intent);
        Assert.Null(status.Error);
        Assert.Equal(FileOperation.None, status.FileOp);
    }

    // ── Phase/Health regression tests ──────────────────────────────────

    [Fact]
    public async Task Status_CheckingFiles_WithError_ShowsCheckingFilesPhase()
    {
        // Bug 3 regression: recheck during error should show CheckingFiles phase, not Error health
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        mt.UpdateStatus(new TorrentStatus
        {
            Phase = TransferPhase.CheckingFiles,
            Intent = UserIntent.Active,
            Error = new TorrentError { Message = "previous error" },
            IsAutoManaged = true
        }, force: true);
        await mt.StateController.DrainAsync();

        var status = mt.GetStatus();
        Assert.Equal(TransferPhase.CheckingFiles, status.Phase);
    }

    [Fact]
    public async Task Status_Downloading_WithError_ShowsDownloadingPhase()
    {
        // Bug 1 regression: active download with stale error health should show Downloading phase
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        mt.UpdateStatus(new TorrentStatus
        {
            Phase = TransferPhase.Downloading,
            Intent = UserIntent.Active,
            Error = new TorrentError { Message = "stale error" },
            IsAutoManaged = true
        }, force: true);
        await mt.StateController.DrainAsync();

        var status = mt.GetStatus();
        Assert.Equal(TransferPhase.Downloading, status.Phase);
    }

    [Fact]
    public async Task Status_Seeding_WithError_ShowsSeedingPhase()
    {
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        mt.UpdateStatus(new TorrentStatus
        {
            Phase = TransferPhase.Seeding,
            Intent = UserIntent.Active,
            Error = new TorrentError { Message = "stale error" },
            IsAutoManaged = true
        }, force: true);
        await mt.StateController.DrainAsync();

        var status = mt.GetStatus();
        Assert.Equal(TransferPhase.Seeding, status.Phase);
    }

    [Fact]
    public async Task Status_Idle_WithError_ShowsErrorHealth()
    {
        // Error DOES show when no active phase is running
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        mt.UpdateStatus(new TorrentStatus
        {
            Phase = TransferPhase.Idle,
            Intent = UserIntent.Active,
            Error = new TorrentError { Message = "real error" },
            IsAutoManaged = true
        }, force: true);
        await mt.StateController.DrainAsync();

        var status = mt.GetStatus();
        Assert.NotNull(status.Error);
    }

    [Fact]
    public async Task Status_Idle_WithNoError_ShowsIdlePhase()
    {
        var mt = new ManagedTorrent("abc123", "TestTorrent");
        mt.UpdateStatus(new TorrentStatus
        {
            Phase = TransferPhase.Idle,
            Intent = UserIntent.Active,
            IsAutoManaged = true
        }, force: true);
        await mt.StateController.DrainAsync();

        var status = mt.GetStatus();
        Assert.Equal(TransferPhase.Idle, status.Phase);
        Assert.Null(status.Error);
    }
}
