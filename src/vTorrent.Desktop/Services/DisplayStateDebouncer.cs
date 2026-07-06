using vTorrent.Desktop.ViewModels;

namespace vTorrent.Desktop.Services;

/// <summary>
/// Smooths the display-state stream so transient startup phases that flash by in milliseconds
/// (Allocating -> Verifying -> Connecting -> Downloading on a fast torrent) are never painted,
/// while a transient phase that genuinely lasts (e.g. a multi-minute recheck) still appears.
///
/// Stateful but fully deterministic: <see cref="Resolve"/> takes the current time as a parameter,
/// so the ViewModel feeds <c>Environment.TickCount64</c> and unit tests feed fake timestamps.
/// One instance per <see cref="TorrentViewModel"/>.
///
/// Heartbeat: the engine's 1-second statistics timer re-pushes the snapshot every second, so a
/// pending transient state is re-evaluated (and eventually committed) even when no other event
/// fires during a long-running phase.
/// </summary>
public sealed class DisplayStateDebouncer
{
    private readonly long _debounceMs;

    private TorrentDisplayState _committed;
    private bool _hasCommitted;

    private TorrentDisplayState _pending;
    private bool _hasPending;
    private long _pendingSinceMs;

    public DisplayStateDebouncer(long debounceMs = 400) => _debounceMs = debounceMs;

    /// <summary>
    /// Given the freshly-derived display state and the current monotonic time, returns the
    /// state that should actually be shown.
    /// </summary>
    public TorrentDisplayState Resolve(TorrentDisplayState derived, long nowMs)
    {
        // Nothing has ever been shown — commit immediately so the row isn't blank.
        if (!_hasCommitted)
        {
            _committed = derived;
            _hasCommitted = true;
            _hasPending = false;
            return _committed;
        }

        // Already showing this; drop any pending candidate.
        if (derived == _committed)
        {
            _hasPending = false;
            return _committed;
        }

        // Destination states win instantly — they are the result the user is waiting for.
        if (!IsTransient(derived))
        {
            _committed = derived;
            _hasPending = false;
            return _committed;
        }

        // Transient state: start (or continue) its window. Show the previous state meanwhile.
        if (!_hasPending || _pending != derived)
        {
            _pending = derived;
            _hasPending = true;
            _pendingSinceMs = nowMs;
            return _committed;
        }

        // Same transient state has now persisted long enough — promote it.
        if (nowMs - _pendingSinceMs >= _debounceMs)
        {
            _committed = derived;
            _hasPending = false;
        }

        return _committed;
    }

    // ─── POLICY ──────────────────────────────────────────────────────────────────────────────
    // Which display states are "transient" (debounced — shown only if they persist) vs.
    // destinations (shown the instant we see them). This list + the debounceMs threshold are the
    // two levers that shape the smoothing behaviour. Trade-off worth knowing: Stalled/StalledSeeding
    // are treated as destinations here, so a momentary 0-rate blip right after Connecting CAN still
    // flash "Stalled" for one stats tick. Add them below to debounce that too — at the cost of a
    // genuine stall taking ~debounceMs longer to appear.
    private static bool IsTransient(TorrentDisplayState state) => state switch
    {
        TorrentDisplayState.Allocating => true,
        TorrentDisplayState.CheckingResumeData => true,
        TorrentDisplayState.Verifying => true,
        TorrentDisplayState.Checking => true,
        TorrentDisplayState.Connecting => true,
        TorrentDisplayState.MetadataDownloading => true,
        TorrentDisplayState.Stopping => true,
        TorrentDisplayState.Moving => true,
        _ => false,
    };
}
