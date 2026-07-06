using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Desktop.ViewModels;
using vTorrent.Desktop.Services;
using Xunit;

namespace vTorrent.Tests.Unit.Core.State;

public class DisplayStateDerivationTests
{
    // Helper — most tests don't care about live metrics, default to "alive" so the
    // stalled branch isn't accidentally triggered when a test only sets phase.
    private static TorrentDisplayState Derive(TorrentStatus s,
        int down = 100, int up = 100, int peers = 1)
        => DisplayStateDeriver.Derive(s, down, up, peers);

    [Fact]
    public void Error_AlwaysWins()
    {
        var status = new TorrentStatus { Phase = TransferPhase.Downloading, Error = new TorrentError { Message = "disk full" } };
        Assert.Equal(TorrentDisplayState.Error, Derive(status));
    }

    [Fact]
    public void MissingFiles_AlwaysWins()
    {
        var status = new TorrentStatus { Phase = TransferPhase.Downloading, MissingFiles = true };
        Assert.Equal(TorrentDisplayState.MissingFiles, Derive(status));
    }

    [Fact]
    public void Paused_OverridesPhase()
    {
        var status = new TorrentStatus { Phase = TransferPhase.Downloading, Intent = UserIntent.Paused };
        Assert.Equal(TorrentDisplayState.Paused, Derive(status));
    }

    [Fact]
    public void Queued_OverridesPhase()
    {
        var status = new TorrentStatus { Phase = TransferPhase.Downloading, Intent = UserIntent.Queued };
        Assert.Equal(TorrentDisplayState.Queued, Derive(status));
    }

    [Fact]
    public void CheckingResumeData_Shown()
    {
        var status = new TorrentStatus { Phase = TransferPhase.CheckingResumeData, Intent = UserIntent.Active };
        Assert.Equal(TorrentDisplayState.CheckingResumeData, Derive(status));
    }

    [Fact]
    public void CheckingFiles_ShownAsVerifying()
    {
        var status = new TorrentStatus { Phase = TransferPhase.CheckingFiles, Intent = UserIntent.Active };
        Assert.Equal(TorrentDisplayState.Verifying, Derive(status));
    }

    [Fact]
    public void Downloading_Stalled()
    {
        // Stalled = downloading with 0 rate AND 0 peers (live metrics).
        var status = new TorrentStatus { Phase = TransferPhase.Downloading, Intent = UserIntent.Active, IsAutoManaged = true };
        Assert.Equal(TorrentDisplayState.Stalled, Derive(status, down: 0, up: 0, peers: 0));
    }

    [Fact]
    public void Downloading_Forced()
    {
        var status = new TorrentStatus { Phase = TransferPhase.Downloading, Intent = UserIntent.Active, IsAutoManaged = false };
        Assert.Equal(TorrentDisplayState.ForcedDownloading, Derive(status, down: 100, up: 0, peers: 1));
    }

    [Fact]
    public void Downloading_Normal()
    {
        var status = new TorrentStatus { Phase = TransferPhase.Downloading, Intent = UserIntent.Active, IsAutoManaged = true };
        Assert.Equal(TorrentDisplayState.Downloading, Derive(status, down: 100, up: 0, peers: 1));
    }

    [Fact]
    public void Seeding_Normal()
    {
        var status = new TorrentStatus { Phase = TransferPhase.Seeding, Intent = UserIntent.Active, IsAutoManaged = true };
        Assert.Equal(TorrentDisplayState.Seeding, Derive(status, down: 0, up: 100, peers: 1));
    }

    [Fact]
    public void Seeding_Stalled()
    {
        // Stalled seeding = 0 upload rate AND 0 peers (live metrics).
        var status = new TorrentStatus { Phase = TransferPhase.Seeding, Intent = UserIntent.Active, IsAutoManaged = true };
        Assert.Equal(TorrentDisplayState.StalledSeeding, Derive(status, down: 0, up: 0, peers: 0));
    }

    [Fact]
    public void Idle_ShowsStopped()
    {
        var status = new TorrentStatus { Phase = TransferPhase.Idle, Intent = UserIntent.Active };
        Assert.Equal(TorrentDisplayState.Stopped, Derive(status));
    }

    [Fact]
    public void MovingWhileDownloading_ShowsMoving()
    {
        var status = new TorrentStatus
        {
            Phase = TransferPhase.Downloading,
            FileOp = FileOperation.Moving,
            Intent = UserIntent.Active,
        };
        Assert.Equal(TorrentDisplayState.Moving, Derive(status));
    }

    // -- Regression: bug where engine never wrote rates/peers into TorrentStateController. --
    // Status struct values can be anything (they're no longer carried on TorrentStatus),
    // but the deriver must classify based on the LIVE metrics passed in. Even with a
    // "fresh / never-updated" looking status (Phase=Downloading, IsAutoManaged=true), if
    // the engine reports peers and rate, the badge is Downloading — not Stalled.
    [Fact]
    public void Downloading_WithLiveActivity_NotStalled()
    {
        var freshlyConstructedStatus = new TorrentStatus
        {
            Phase = TransferPhase.Downloading,
            Intent = UserIntent.Active,
            IsAutoManaged = true,
        };

        Assert.Equal(
            TorrentDisplayState.Downloading,
            DisplayStateDeriver.Derive(freshlyConstructedStatus, downloadRate: 1234, uploadRate: 0, connectedPeers: 5));
    }
}
