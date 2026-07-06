using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Core.State;
using Xunit;

namespace vTorrent.Core.Tests.State;

public class TorrentStateControllerTests : IAsyncDisposable
{
    private readonly TorrentStateController _controller;

    public TorrentStateControllerTests()
    {
        _controller = new TorrentStateController(NullLogger<TorrentStateController>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _controller.DisposeAsync();
    }

    [Fact]
    public void InitialState_IsIdle()
    {
        var status = _controller.GetStatus();
        status.Phase.Should().Be(TransferPhase.Idle);
        status.Intent.Should().Be(UserIntent.Paused);
        status.FileOp.Should().Be(FileOperation.None);
        status.Error.Should().BeNull();
    }

    [Fact]
    public async Task PostPhase_Allocate_TransitionsFromIdle()
    {
        _controller.PostPhase(PhaseTrigger.Allocate);
        await _controller.DrainAsync();

        _controller.GetStatus().Phase.Should().Be(TransferPhase.Allocating);
    }

    [Fact]
    public async Task PostPhase_InvalidTransition_LogsAndSkips()
    {
        _controller.PostPhase(PhaseTrigger.Complete);
        await _controller.DrainAsync();

        _controller.GetStatus().Phase.Should().Be(TransferPhase.Idle);
    }

    [Fact]
    public async Task PostIntent_ChangesIntent()
    {
        _controller.PostIntent(IntentTrigger.Activate);
        await _controller.DrainAsync();

        _controller.GetStatus().Intent.Should().Be(UserIntent.Active);
    }

    [Fact]
    public async Task PostError_SetsErrorAndResetsPhase()
    {
        _controller.PostPhase(PhaseTrigger.Allocate);
        _controller.PostPhase(PhaseTrigger.CheckFiles);
        _controller.PostPhase(PhaseTrigger.Connect);
        _controller.PostPhase(PhaseTrigger.StartDownloading);
        await _controller.DrainAsync();
        _controller.GetStatus().Phase.Should().Be(TransferPhase.Downloading);

        _controller.PostError(new TorrentError { Message = "Disk full", ErrorCode = "DiskFull" });
        await _controller.DrainAsync();

        var status = _controller.GetStatus();
        status.Phase.Should().Be(TransferPhase.Idle);
        status.Error.Should().NotBeNull();
        status.Error!.Value.Message.Should().Be("Disk full");
    }

    [Fact]
    public async Task PostClearError_ClearsError()
    {
        _controller.PostError(new TorrentError { Message = "Disk full" });
        await _controller.DrainAsync();
        _controller.GetStatus().Error.Should().NotBeNull();

        _controller.PostClearError();
        await _controller.DrainAsync();
        _controller.GetStatus().Error.Should().BeNull();
    }

    [Fact]
    public async Task PostFileOp_StartMove_TransitionsFromNone()
    {
        _controller.PostFileOp(FileOpTrigger.StartMove);
        await _controller.DrainAsync();

        _controller.GetStatus().FileOp.Should().Be(FileOperation.Moving);
    }

    [Fact]
    public async Task PostFileOp_InvalidDoubleStart_LogsAndSkips()
    {
        _controller.PostFileOp(FileOpTrigger.StartMove);
        await _controller.DrainAsync();

        _controller.PostFileOp(FileOpTrigger.StartRecheck);
        await _controller.DrainAsync();

        _controller.GetStatus().FileOp.Should().Be(FileOperation.Moving);
    }

    [Fact]
    public async Task StatusChanged_FiresOnTransition()
    {
        StatusChangedEventArgs? captured = null;
        _controller.StatusChanged += (_, args) => captured = args;

        _controller.PostPhase(PhaseTrigger.Allocate);
        await _controller.DrainAsync();

        captured.Should().NotBeNull();
        captured!.OldStatus.Phase.Should().Be(TransferPhase.Idle);
        captured!.NewStatus.Phase.Should().Be(TransferPhase.Allocating);
    }

    [Fact]
    public async Task MetricsUpdate_UpdatesFileOpProgress()
    {
        _controller.PostMetrics(fileOpProgress: 0.5);
        await _controller.DrainAsync();

        var status = _controller.GetStatus();
        status.FileOpProgress.Should().Be(0.5);
    }

    [Fact]
    public async Task PostIntent_RefiringCurrentIntent_SafelyIgnored()
    {
        _controller.PostIntent(IntentTrigger.Activate);
        await _controller.DrainAsync();
        _controller.GetStatus().Intent.Should().Be(UserIntent.Active);

        _controller.PostIntent(IntentTrigger.Activate);
        await _controller.DrainAsync();
        _controller.GetStatus().Intent.Should().Be(UserIntent.Active);
    }

    [Fact]
    public async Task PostPhase_StopFromAllocating_GoesToStopping()
    {
        _controller.PostPhase(PhaseTrigger.Allocate);
        await _controller.DrainAsync();
        _controller.GetStatus().Phase.Should().Be(TransferPhase.Allocating);

        _controller.PostPhase(PhaseTrigger.Stop);
        await _controller.DrainAsync();
        _controller.GetStatus().Phase.Should().Be(TransferPhase.Stopping);
    }

    [Fact]
    public async Task FullLifecycle_NormalTorrent()
    {
        _controller.PostIntent(IntentTrigger.Activate);
        _controller.PostPhase(PhaseTrigger.Allocate);
        _controller.PostPhase(PhaseTrigger.CheckFiles);
        _controller.PostPhase(PhaseTrigger.Connect);
        _controller.PostPhase(PhaseTrigger.StartDownloading);
        _controller.PostPhase(PhaseTrigger.Complete);
        await _controller.DrainAsync();

        var status = _controller.GetStatus();
        status.Phase.Should().Be(TransferPhase.Seeding);
        status.Intent.Should().Be(UserIntent.Active);
        status.Error.Should().BeNull();
    }

    [Fact]
    public async Task FullLifecycle_StopAndRestart()
    {
        _controller.PostPhase(PhaseTrigger.Allocate);
        _controller.PostPhase(PhaseTrigger.CheckFiles);
        _controller.PostPhase(PhaseTrigger.Connect);
        _controller.PostPhase(PhaseTrigger.StartDownloading);
        await _controller.DrainAsync();
        _controller.GetStatus().Phase.Should().Be(TransferPhase.Downloading);

        _controller.PostPhase(PhaseTrigger.Stop);
        _controller.PostPhase(PhaseTrigger.Stopped);
        await _controller.DrainAsync();
        _controller.GetStatus().Phase.Should().Be(TransferPhase.Idle);

        _controller.PostPhase(PhaseTrigger.Allocate);
        await _controller.DrainAsync();
        _controller.GetStatus().Phase.Should().Be(TransferPhase.Allocating);
    }
}
