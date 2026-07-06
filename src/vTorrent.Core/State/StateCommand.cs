using System.Threading.Tasks;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.State;

/// <summary>
/// Base type for commands posted to <see cref="TorrentStateController"/>'s channel.
/// Each command maps to one state machine mutation.
/// </summary>
internal abstract record StateCommand;

/// <summary>Fire a trigger on the Phase state machine.</summary>
internal sealed record PhaseCommand(PhaseTrigger Trigger) : StateCommand;

/// <summary>Fire a trigger on the Intent state machine.</summary>
internal sealed record IntentCommand(IntentTrigger Trigger) : StateCommand;

/// <summary>Fire a trigger on the FileOp state machine.</summary>
internal sealed record FileOpCommand(FileOpTrigger Trigger) : StateCommand;

/// <summary>Set an error and reset phase to Idle.</summary>
internal sealed record SetErrorCommand(TorrentError Error) : StateCommand;

/// <summary>Clear the current error.</summary>
internal sealed record ClearErrorCommand : StateCommand;

/// <summary>Set the MissingFiles flag.</summary>
internal sealed record SetMissingFilesCommand(bool MissingFiles) : StateCommand;

/// <summary>Update state-machine-owned metrics (file-op progress, completion flags).
/// Live transfer metrics (DownloadRate/UploadRate/ConnectedPeers) are not state-machine state —
/// they live on <see cref="TorrentSnapshot"/> sourced from the engine.</summary>
internal sealed record MetricsCommand(
    double? FileOpProgress = null,
    bool? IsFinished = null,
    bool? IsSeed = null) : StateCommand;

/// <summary>Set the IsAutoManaged flag.</summary>
internal sealed record SetAutoManagedCommand(bool IsAutoManaged) : StateCommand;

/// <summary>Restore full state on startup — bypasses transition graph, sets machines directly.</summary>
internal sealed record RestoreStateCommand(
    TransferPhase Phase,
    UserIntent Intent,
    FileOperation FileOp,
    TorrentError? Error = null,
    bool MissingFiles = false,
    bool IsAutoManaged = true) : StateCommand;

/// <summary>Internal drain command for testing — signals completion when processed.</summary>
internal sealed record DrainCommand(TaskCompletionSource Completion) : StateCommand;
