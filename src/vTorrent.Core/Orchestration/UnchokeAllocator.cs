using System;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Distributes unchoke slots across all torrents.
/// Global cap on total unchoked peers, matching libtorrent's unchoke_slots_limit.
/// Pattern: same as ConnectionAllocator.
/// </summary>
public class UnchokeAllocator
{
    private readonly object _lock = new();
    private int _totalUnchokedPeers;

    /// <summary>Global limit on total unchoked peers across all torrents. libtorrent default: 8.</summary>
    public int MaxGlobalUnchokeSlots { get; set; } = 8;

    /// <summary>Current total unchoked peers across all torrents.</summary>
    public int TotalUnchokedPeers
    {
        get { lock (_lock) return _totalUnchokedPeers; }
    }

    /// <summary>Available unchoke slots remaining.</summary>
    public int AvailableSlots
    {
        get { lock (_lock) return Math.Max(0, MaxGlobalUnchokeSlots - _totalUnchokedPeers); }
    }

    /// <summary>
    /// Request permission to unchoke a peer.
    /// </summary>
    /// <returns>True if unchoke is allowed under the global cap.</returns>
    public bool TryAcquireUnchokeSlot()
    {
        lock (_lock)
        {
            if (_totalUnchokedPeers >= MaxGlobalUnchokeSlots)
                return false;

            _totalUnchokedPeers++;
            return true;
        }
    }

    /// <summary>
    /// Release an unchoke slot when a peer is choked or disconnected.
    /// </summary>
    public void ReleaseUnchokeSlot()
    {
        lock (_lock)
        {
            _totalUnchokedPeers = Math.Max(0, _totalUnchokedPeers - 1);
        }
    }

    /// <summary>
    /// Reset the counter (e.g., on session restart).
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _totalUnchokedPeers = 0;
        }
    }
}
