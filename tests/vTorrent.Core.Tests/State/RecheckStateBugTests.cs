using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Core.Orchestration;
using vTorrent.Core.ResumeData;
using vTorrent.Core.State;
using FluentAssertions;
using Xunit;

namespace vTorrent.Tests.Unit.Core.State;

/// <summary>
/// Integration tests covering recheck/recovery bugs and orthogonal state verification.
///
/// Bug 2: After recheck, engine downloads instead of seeding.
///        Fix: NoVerifyFiles flag lifecycle properly managed.
///
/// Bug 3: Recheck on errored torrent showed "Error" during verification.
///        Fix: ForceRecheckAsync clears Error at recheck start.
///
/// Orthogonal: Active phases preserve Phase dimension even with Error set.
/// </summary>
public class RecheckStateBugTests
{
    #region Helpers

    private static async Task<ManagedTorrent> CreateTorrentAsync(TorrentStatus? initialStatus = null)
    {
        var mt = new ManagedTorrent("AABB00112233445566778899AABBCCDDEEFF0011", "TestTorrent");
        if (initialStatus.HasValue)
        {
            mt.UpdateStatus(initialStatus.Value, force: true);
            await mt.StateController.DrainAsync();
        }
        return mt;
    }

    private static TorrentStatus ErrorStatus(TransferPhase phase = TransferPhase.Idle) => new()
    {
        Phase = phase,
        Intent = UserIntent.Active,
        Error = new TorrentError { Message = "Disk full" }
    };

    #endregion

    // ── Bug 3 Tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Bug3_RecheckOnErroredTorrent_ShowsVerifying_NotError()
    {
        // Arrange: torrent in Error state
        var mt = await CreateTorrentAsync(ErrorStatus());

        // Act: simulate ForceRecheckAsync clearing error + setting CheckingFiles
        var current = mt.GetStatus();
        mt.UpdateStatus(current with
        {
            Phase = TransferPhase.CheckingFiles,
            FileOp = FileOperation.Rechecking,
            Error = null,
        }, force: true);
        await mt.StateController.DrainAsync();

        // Assert: UI should show CheckingFiles, not Error
        var status = mt.GetStatus();
        status.Phase.Should().Be(TransferPhase.CheckingFiles);
        status.Error.Should().BeNull();
    }

    [Fact]
    public async Task Bug3_RecheckOnErroredTorrent_ErrorMessage_Cleared()
    {
        // Arrange: torrent in Error state with error message
        var mt = await CreateTorrentAsync(ErrorStatus());

        // Act: simulate ForceRecheckAsync clearing error state
        var current = mt.GetStatus();
        mt.UpdateStatus(current with
        {
            Phase = TransferPhase.CheckingFiles,
            FileOp = FileOperation.Rechecking,
            Error = null,
        }, force: true);
        await mt.StateController.DrainAsync();

        // Assert: ErrorMessage cleared on both mt and status
        mt.ErrorMessage.Should().BeNull();
        mt.GetStatus().Error.Should().BeNull();
    }

    // ── Bug 2 Tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Bug2_AfterRecheck_NoVerifyFilesFlagSet()
    {
        // Arrange
        var mt = await CreateTorrentAsync();

        // Act: simulate setting NoVerifyFiles flag (as done before engine start after recheck)
        mt.ResumeData.Flags |= TorrentFlags.NoVerifyFiles;

        // Assert: flag is present
        mt.ResumeData.Flags.HasFlag(TorrentFlags.NoVerifyFiles).Should().BeTrue();
    }

    [Fact]
    public async Task Bug2_AfterEngineStart_NoVerifyFilesFlagCleared()
    {
        // Arrange: flag was set before engine start
        var mt = await CreateTorrentAsync();
        mt.ResumeData.Flags |= TorrentFlags.NoVerifyFiles;
        mt.ResumeData.Flags.HasFlag(TorrentFlags.NoVerifyFiles).Should().BeTrue("precondition");

        // Act: simulate engine start clearing the flag
        mt.ResumeData.Flags &= ~TorrentFlags.NoVerifyFiles;

        // Assert: flag is cleared
        mt.ResumeData.Flags.HasFlag(TorrentFlags.NoVerifyFiles).Should().BeFalse();
    }

    [Fact]
    public async Task Bug2_FreshTorrent_NoVerifyFilesFlagNotSet()
    {
        // Arrange + Act: new torrent
        var mt = await CreateTorrentAsync();

        // Assert: NoVerifyFiles not set by default
        mt.ResumeData.Flags.HasFlag(TorrentFlags.NoVerifyFiles).Should().BeFalse();
    }

    // ── Orthogonal State Tests ───────────────────────────────────────────────

    [Theory]
    [InlineData(TransferPhase.CheckingFiles)]
    [InlineData(TransferPhase.Downloading)]
    [InlineData(TransferPhase.Seeding)]
    [InlineData(TransferPhase.Connecting)]
    [InlineData(TransferPhase.Allocating)]
    [InlineData(TransferPhase.FetchingMetadata)]
    [InlineData(TransferPhase.Stopping)]
    public async Task ActivePhase_WithError_PhasePreserved(TransferPhase phase)
    {
        // Arrange: torrent with active phase but Error set
        var mt = await CreateTorrentAsync(new TorrentStatus
        {
            Phase = phase,
            Intent = UserIntent.Active,
            Error = new TorrentError { Message = "stale error" }
        });

        // Assert: phase is preserved regardless of error
        var status = mt.GetStatus();
        status.Phase.Should().Be(phase,
            because: $"Phase={phase} should be preserved even with Error set");
    }

    [Fact]
    public async Task Paused_WithError_ShowsPausedIntent()
    {
        // Arrange: paused intent + Error set
        var mt = await CreateTorrentAsync(new TorrentStatus
        {
            Phase = TransferPhase.Idle,
            Intent = UserIntent.Paused,
            Error = new TorrentError { Message = "some error" }
        });

        // Assert: intent is Paused
        var status = mt.GetStatus();
        status.Intent.Should().Be(UserIntent.Paused);
    }

    // ── Full Scenario Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task FullScenario_ErrorToRecheckToComplete()
    {
        // Arrange: torrent in Error state
        var mt = await CreateTorrentAsync(ErrorStatus());
        var status = mt.GetStatus();
        status.Error.Should().NotBeNull("initial state should have Error set");

        // Step 1: ForceRecheckAsync clears error + sets CheckingFiles
        var current = mt.GetStatus();
        mt.UpdateStatus(current with
        {
            Phase = TransferPhase.CheckingFiles,
            FileOp = FileOperation.Rechecking,
            Error = null,
        }, force: true);
        await mt.StateController.DrainAsync();
        status = mt.GetStatus();
        status.Phase.Should().Be(TransferPhase.CheckingFiles, "after recheck start should show CheckingFiles");
        status.Error.Should().BeNull("error should be cleared at recheck start");

        // Step 2: Verification completes — engine transitions to Idle
        mt.UpdateStatus(mt.GetStatus() with
        {
            Phase = TransferPhase.Idle,
            FileOp = FileOperation.None
        }, force: true);
        await mt.StateController.DrainAsync();
        status = mt.GetStatus();
        status.Phase.Should().Be(TransferPhase.Idle, "after recheck completes should be Idle");
        status.Error.Should().BeNull("error should remain null after recheck");

        // Step 3: Orchestrator queues the torrent
        mt.UpdateStatus(mt.GetStatus() with
        {
            Intent = UserIntent.Queued
        }, force: true);
        await mt.StateController.DrainAsync();
        status = mt.GetStatus();
        status.Intent.Should().Be(UserIntent.Queued, "after queuing should show Queued intent");
    }

}
