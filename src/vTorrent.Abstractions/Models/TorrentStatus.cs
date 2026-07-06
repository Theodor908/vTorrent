using vTorrent.Abstractions.Enums;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// Atomic, immutable snapshot of all torrent state dimensions.
/// Single source of truth — swapped atomically, never partially updated.
/// </summary>
public readonly record struct TorrentStatus
{
    // Orthogonal dimensions
    public TransferPhase   Phase          { get; init; }
    public FileOperation   FileOp         { get; init; }
    public UserIntent      Intent         { get; init; }

    // Error (replaces Health + ErrorMessage)
    public TorrentError?   Error          { get; init; }

    // Missing files flag (previously Health == MissingFiles)
    public bool            MissingFiles   { get; init; }

    // Flags
    public bool            IsAutoManaged  { get; init; }
    public bool            IsFinished     { get; init; }
    public bool            IsSeed         { get; init; }

    // File operation progress (recheck/move) — owned by the state machine,
    // updated via PostMetrics(fileOpProgress). Live transfer metrics
    // (DownloadRate/UploadRate/ConnectedPeers/Progress) live on TorrentSnapshot.
    public double          FileOpProgress { get; init; }

    /// <summary>
    /// Default idle status — engine not running, no errors.
    /// </summary>
    public static TorrentStatus Idle => new()
    {
        Phase = TransferPhase.Idle,
        FileOp = FileOperation.None,
        Intent = UserIntent.Paused,
        IsAutoManaged = true
    };
}
