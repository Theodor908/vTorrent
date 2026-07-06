using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Core.State;
using Xunit;

namespace vTorrent.Tests.Unit.Core.State;

public class TorrentStatusTests
{
    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new TorrentStatus { Phase = TransferPhase.Downloading, Intent = UserIntent.Active };
        var b = new TorrentStatus { Phase = TransferPhase.Downloading, Intent = UserIntent.Active };
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Equality_DifferentPhase_AreNotEqual()
    {
        var a = new TorrentStatus { Phase = TransferPhase.Downloading };
        var b = new TorrentStatus { Phase = TransferPhase.Seeding };
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void WithExpression_ChangesOneDimension_PreservesOthers()
    {
        var original = new TorrentStatus
        {
            Phase = TransferPhase.Downloading,
            FileOp = FileOperation.Moving,
            Intent = UserIntent.Active,
            FileOpProgress = 0.5,
        };
        var updated = original with { FileOp = FileOperation.None };
        Assert.Equal(TransferPhase.Downloading, updated.Phase);
        Assert.Equal(FileOperation.None, updated.FileOp);
        Assert.Equal(UserIntent.Active, updated.Intent);
        Assert.Equal(0.5, updated.FileOpProgress);
    }

    [Fact]
    public void Idle_ReturnsDefaultIdleStatus()
    {
        var idle = TorrentStatus.Idle;
        Assert.Equal(TransferPhase.Idle, idle.Phase);
        Assert.Equal(FileOperation.None, idle.FileOp);
        Assert.Equal(UserIntent.Paused, idle.Intent);
        Assert.Null(idle.Error);
        Assert.True(idle.IsAutoManaged);
    }

    [Fact]
    public void StatusChangedEventArgs_DetectsChangedDimensions()
    {
        var old = new TorrentStatus { Phase = TransferPhase.Downloading };
        var next = new TorrentStatus { Phase = TransferPhase.Downloading, Error = new TorrentError { Message = "disk full" } };
        var args = new StatusChangedEventArgs(old, next);
        Assert.False(args.PhaseChanged);
        Assert.True(args.ErrorChanged);
        Assert.False(args.IntentChanged);
        Assert.False(args.FileOpChanged);
    }
}
