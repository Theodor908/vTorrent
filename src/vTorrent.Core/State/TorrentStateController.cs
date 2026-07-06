using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stateless;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.State;

/// <summary>
/// Active Object that owns all state mutations for a single torrent.
/// Three Stateless state machines (Phase, Intent, FileOp) + error field.
/// All mutations serialized through a Channel — single consumer, no locks.
/// </summary>
internal sealed class TorrentStateController : IAsyncDisposable
{
    private readonly ILogger _logger;
    private StateMachine<TransferPhase, PhaseTrigger> _phaseSm;
    private StateMachine<UserIntent, IntentTrigger> _intentSm;
    private StateMachine<FileOperation, FileOpTrigger> _fileOpSm;
    private bool _stateChanged;

    private TorrentError? _error;
    private bool _missingFiles;

    // State-machine-owned metrics. Live transfer metrics
    // (DownloadRate/UploadRate/ConnectedPeers) are sourced from the engine via
    // TorrentSnapshot — they are not part of the state-machine snapshot.
    private double _fileOpProgress;
    private bool _isFinished;
    private bool _isSeed;
    private bool _isAutoManaged = true;

    // Channel serialization
    private readonly Channel<StateCommand> _channel = Channel.CreateUnbounded<StateCommand>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _consumerLoop;

    // Atomic snapshot for external readers
    private TorrentStatus _snapshot;
    private readonly object _snapshotLock = new();

    /// <summary>Fired from the consumer loop after each state change.</summary>
    public event EventHandler<StatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// Fired after a batch of commands is processed and the channel is empty.
    /// Used for deferred auto-manager recalculation.
    /// </summary>
    public event Action? ChannelDrained;

    public TorrentStateController(ILogger logger, TransferPhase initialPhase = TransferPhase.Idle,
        UserIntent initialIntent = UserIntent.Paused)
    {
        _logger = logger;

        _phaseSm = new StateMachine<TransferPhase, PhaseTrigger>(initialPhase);
        _intentSm = new StateMachine<UserIntent, IntentTrigger>(initialIntent);
        _fileOpSm = new StateMachine<FileOperation, FileOpTrigger>(FileOperation.None);

        ConfigurePhaseMachine();
        ConfigureIntentMachine();
        ConfigureFileOpMachine();

        _snapshot = BuildStatus();
        _consumerLoop = Task.Run(ProcessCommandsAsync);
    }

    // --- Public API (thread-safe, non-blocking) ---

    public void PostPhase(PhaseTrigger trigger)
        => _channel.Writer.TryWrite(new PhaseCommand(trigger));

    public void PostIntent(IntentTrigger trigger)
        => _channel.Writer.TryWrite(new IntentCommand(trigger));

    public void PostFileOp(FileOpTrigger trigger)
        => _channel.Writer.TryWrite(new FileOpCommand(trigger));

    public void PostError(TorrentError error)
        => _channel.Writer.TryWrite(new SetErrorCommand(error));

    public void PostClearError()
        => _channel.Writer.TryWrite(new ClearErrorCommand());

    public void PostMissingFiles(bool missing)
        => _channel.Writer.TryWrite(new SetMissingFilesCommand(missing));

    public void PostMetrics(double? fileOpProgress = null,
        bool? isFinished = null, bool? isSeed = null)
        => _channel.Writer.TryWrite(new MetricsCommand(fileOpProgress, isFinished, isSeed));

    public void PostAutoManaged(bool isAutoManaged)
        => _channel.Writer.TryWrite(new SetAutoManagedCommand(isAutoManaged));

    public void PostRestore(TransferPhase phase, UserIntent intent, FileOperation fileOp,
        TorrentError? error = null, bool missingFiles = false, bool isAutoManaged = true)
        => _channel.Writer.TryWrite(new RestoreStateCommand(phase, intent, fileOp, error, missingFiles, isAutoManaged));

    /// <summary>Atomic snapshot — safe from any thread.</summary>
    public TorrentStatus GetStatus()
    {
        lock (_snapshotLock) return _snapshot;
    }

    /// <summary>Wait for all pending commands to be processed. Test helper.</summary>
    internal async Task DrainAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _channel.Writer.TryWrite(new DrainCommand(tcs));
        await tcs.Task.ConfigureAwait(false);
    }

    // --- Consumer loop ---

    private async Task ProcessCommandsAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var cmd))
                {
                    if (cmd is DrainCommand drain)
                    {
                        drain.Completion.SetResult();
                        continue;
                    }

                    TorrentStatus old;
                    lock (_snapshotLock) old = _snapshot;

                    try
                    {
                        switch (cmd)
                        {
                            case PhaseCommand c:
                                _phaseSm.Fire(c.Trigger);
                                break;
                            case IntentCommand c:
                                _intentSm.Fire(c.Trigger);
                                break;
                            case FileOpCommand c:
                                _fileOpSm.Fire(c.Trigger);
                                break;
                            case SetErrorCommand c:
                                _error = c.Error;
                                if (_phaseSm.State != TransferPhase.Idle)
                                    _phaseSm.Fire(PhaseTrigger.Reset);
                                break;
                            case ClearErrorCommand:
                                _error = null;
                                break;
                            case SetMissingFilesCommand c:
                                _missingFiles = c.MissingFiles;
                                break;
                            case MetricsCommand c:
                                if (c.FileOpProgress.HasValue) _fileOpProgress = c.FileOpProgress.Value;
                                if (c.IsFinished.HasValue) _isFinished = c.IsFinished.Value;
                                if (c.IsSeed.HasValue) _isSeed = c.IsSeed.Value;
                                break;
                            case SetAutoManagedCommand c:
                                _isAutoManaged = c.IsAutoManaged;
                                break;
                            case RestoreStateCommand c:
                                _phaseSm = new StateMachine<TransferPhase, PhaseTrigger>(c.Phase);
                                ConfigurePhaseMachine();
                                _intentSm = new StateMachine<UserIntent, IntentTrigger>(c.Intent);
                                ConfigureIntentMachine();
                                _fileOpSm = new StateMachine<FileOperation, FileOpTrigger>(c.FileOp);
                                ConfigureFileOpMachine();
                                _error = c.Error;
                                _missingFiles = c.MissingFiles;
                                _isAutoManaged = c.IsAutoManaged;
                                break;
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger.LogWarning(ex,
                            "Invalid state transition rejected: {Command} in {Phase}/{Intent}/{FileOp}",
                            cmd, _phaseSm.State, _intentSm.State, _fileOpSm.State);
                        continue;
                    }

                    var current = BuildStatus();
                    lock (_snapshotLock) _snapshot = current;

                    if (old != current)
                    {
                        StatusChanged?.Invoke(this, new StatusChangedEventArgs(old, current));
                        _stateChanged = true;
                    }
                }

                // Batch boundary: inner TryRead loop drained. Fire ChannelDrained if state changed.
                if (_stateChanged)
                {
                    _stateChanged = false;
                    ChannelDrained?.Invoke();
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    // --- State machine configuration ---

    private void ConfigurePhaseMachine()
    {
        _phaseSm.Configure(TransferPhase.Idle)
            .Permit(PhaseTrigger.Allocate, TransferPhase.Allocating)
            .Permit(PhaseTrigger.CheckResume, TransferPhase.CheckingResumeData)
            .Permit(PhaseTrigger.FetchMetadata, TransferPhase.FetchingMetadata);

        _phaseSm.Configure(TransferPhase.Allocating)
            .Permit(PhaseTrigger.CheckFiles, TransferPhase.CheckingFiles)
            .Permit(PhaseTrigger.Stop, TransferPhase.Stopping)
            .Permit(PhaseTrigger.Reset, TransferPhase.Idle);

        _phaseSm.Configure(TransferPhase.CheckingResumeData)
            .Permit(PhaseTrigger.CheckFiles, TransferPhase.CheckingFiles)
            .Permit(PhaseTrigger.Connect, TransferPhase.Connecting)
            .Permit(PhaseTrigger.Stop, TransferPhase.Stopping)
            .Permit(PhaseTrigger.Reset, TransferPhase.Idle);

        _phaseSm.Configure(TransferPhase.CheckingFiles)
            .Permit(PhaseTrigger.Connect, TransferPhase.Connecting)
            .Permit(PhaseTrigger.Stop, TransferPhase.Stopping)
            .Permit(PhaseTrigger.Reset, TransferPhase.Idle);

        _phaseSm.Configure(TransferPhase.FetchingMetadata)
            .Permit(PhaseTrigger.Allocate, TransferPhase.Allocating)
            .Permit(PhaseTrigger.CheckResume, TransferPhase.CheckingResumeData)
            .Permit(PhaseTrigger.Stop, TransferPhase.Stopping)
            .Permit(PhaseTrigger.Reset, TransferPhase.Idle);

        _phaseSm.Configure(TransferPhase.Connecting)
            .Permit(PhaseTrigger.StartDownloading, TransferPhase.Downloading)
            .Permit(PhaseTrigger.StartSeeding, TransferPhase.Seeding)
            .Permit(PhaseTrigger.Stop, TransferPhase.Stopping)
            .Permit(PhaseTrigger.Reset, TransferPhase.Idle);

        _phaseSm.Configure(TransferPhase.Downloading)
            .Permit(PhaseTrigger.Complete, TransferPhase.Seeding)
            .Permit(PhaseTrigger.Stop, TransferPhase.Stopping)
            .Permit(PhaseTrigger.Reset, TransferPhase.Idle);

        _phaseSm.Configure(TransferPhase.Seeding)
            .Permit(PhaseTrigger.Uncomplete, TransferPhase.Downloading)
            .Permit(PhaseTrigger.Stop, TransferPhase.Stopping)
            .Permit(PhaseTrigger.Reset, TransferPhase.Idle);

        _phaseSm.Configure(TransferPhase.Stopping)
            .Permit(PhaseTrigger.Stopped, TransferPhase.Idle);
    }

    private void ConfigureIntentMachine()
    {
        _intentSm.Configure(UserIntent.Active)
            .Permit(IntentTrigger.Pause, UserIntent.Paused)
            .Permit(IntentTrigger.Queue, UserIntent.Queued);

        _intentSm.Configure(UserIntent.Paused)
            .Permit(IntentTrigger.Activate, UserIntent.Active)
            .Permit(IntentTrigger.Queue, UserIntent.Queued);

        _intentSm.Configure(UserIntent.Queued)
            .Permit(IntentTrigger.Activate, UserIntent.Active)
            .Permit(IntentTrigger.Pause, UserIntent.Paused);
    }

    private void ConfigureFileOpMachine()
    {
        _fileOpSm.Configure(FileOperation.None)
            .Permit(FileOpTrigger.StartMove, FileOperation.Moving)
            .Permit(FileOpTrigger.StartRecheck, FileOperation.Rechecking);

        _fileOpSm.Configure(FileOperation.Moving)
            .Permit(FileOpTrigger.Finish, FileOperation.None);

        _fileOpSm.Configure(FileOperation.Rechecking)
            .Permit(FileOpTrigger.Finish, FileOperation.None);
    }

    private TorrentStatus BuildStatus() => new()
    {
        Phase = _phaseSm.State,
        Intent = _intentSm.State,
        FileOp = _fileOpSm.State,
        Error = _error,
        MissingFiles = _missingFiles,
        IsAutoManaged = _isAutoManaged,
        IsFinished = _isFinished,
        IsSeed = _isSeed,
        FileOpProgress = _fileOpProgress,
    };

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _consumerLoop.ConfigureAwait(false);
    }
}
