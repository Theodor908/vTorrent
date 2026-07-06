using FluentAssertions;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Core.Orchestration;
using vTorrent.Core.State;
using Xunit;

namespace vTorrent.Tests.Unit.Core.Engine;

public class EngineStateRewriteTests
{
    // ================================================================
    // Phase mapping tests (via ManagedTorrent proxy)
    // ================================================================

    [Theory]
    [InlineData(TransferPhase.Idle)]
    [InlineData(TransferPhase.Allocating)]
    [InlineData(TransferPhase.CheckingFiles)]
    [InlineData(TransferPhase.CheckingResumeData)]
    [InlineData(TransferPhase.Connecting)]
    [InlineData(TransferPhase.Downloading)]
    [InlineData(TransferPhase.Seeding)]
    [InlineData(TransferPhase.Stopping)]
    [InlineData(TransferPhase.FetchingMetadata)]
    public async Task UpdateStatus_HealthyPhase_MapsCorrectly(TransferPhase phase)
    {
        var mt = new ManagedTorrent("AA00BB11CC22DD33EE44FF5566778899AABB0011", "Test");
        mt.UpdateStatus(new TorrentStatus
        {
            Phase = phase,
            Intent = UserIntent.Active,
        }, force: true);
        await mt.StateController.DrainAsync();
        var status = mt.GetStatus();
        status.Phase.Should().Be(phase);
        status.Error.Should().BeNull();
    }

    [Fact]
    public async Task UpdateStatus_IdleWithError_ReturnsError()
    {
        var mt = new ManagedTorrent("AA00BB11CC22DD33EE44FF5566778899AABB0011", "Test");
        mt.UpdateStatus(new TorrentStatus
        {
            Phase = TransferPhase.Idle,
            Intent = UserIntent.Active,
            Error = new TorrentError { Message = "disk full" }
        }, force: true);
        await mt.StateController.DrainAsync();
        var status = mt.GetStatus();
        status.Error.Should().NotBeNull();
    }

    [Theory]
    [InlineData(TransferPhase.Downloading)]
    [InlineData(TransferPhase.Seeding)]
    [InlineData(TransferPhase.CheckingFiles)]
    [InlineData(TransferPhase.Connecting)]
    [InlineData(TransferPhase.Allocating)]
    public async Task UpdateStatus_ActivePhaseWithError_PhasePreserved(TransferPhase phase)
    {
        var mt = new ManagedTorrent("AA00BB11CC22DD33EE44FF5566778899AABB0011", "Test");
        mt.UpdateStatus(new TorrentStatus
        {
            Phase = phase,
            Intent = UserIntent.Active,
            Error = new TorrentError { Message = "error" }
        }, force: true);
        await mt.StateController.DrainAsync();
        var status = mt.GetStatus();
        status.Phase.Should().Be(phase,
            $"active phase {phase} should be preserved even with error");
    }
}
