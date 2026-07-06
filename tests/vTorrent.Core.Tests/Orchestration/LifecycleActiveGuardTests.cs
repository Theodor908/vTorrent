using FluentAssertions;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Core.Orchestration;
using Xunit;

namespace vTorrent.Tests.Unit.Core.Orchestration;

public class LifecycleActiveGuardTests
{
    private static TorrentStatus Status(TransferPhase phase, UserIntent intent) => new()
    {
        Phase = phase,
        Intent = intent,
        FileOp = FileOperation.None
    };

    [Theory]
    [InlineData(TransferPhase.Downloading, UserIntent.Active, true)]
    [InlineData(TransferPhase.Seeding, UserIntent.Active, true)]
    // Regression: paused torrents keep their phase under the orthogonal state
    // model. Treating them as "already running" made resume a silent no-op.
    [InlineData(TransferPhase.Downloading, UserIntent.Paused, false)]
    [InlineData(TransferPhase.Seeding, UserIntent.Paused, false)]
    [InlineData(TransferPhase.Downloading, UserIntent.Queued, false)]
    [InlineData(TransferPhase.Idle, UserIntent.Active, false)]
    [InlineData(TransferPhase.Connecting, UserIntent.Active, false)]
    [InlineData(TransferPhase.Stopping, UserIntent.Active, false)]
    public void IsActivelyTransferring_RequiresActiveIntentAndTransferPhase(
        TransferPhase phase, UserIntent intent, bool expected)
    {
        TorrentLifecycleManager.IsActivelyTransferring(Status(phase, intent))
            .Should().Be(expected);
    }
}
