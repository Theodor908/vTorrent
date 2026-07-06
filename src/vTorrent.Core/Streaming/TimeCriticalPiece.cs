using System;

namespace vTorrent.Core.Streaming;

/// <summary>
/// Represents a piece with a delivery deadline for streaming playback.
/// Modeled after libtorrent's time_critical_piece (torrent.hpp:136-158).
/// Sorted by deadline — earliest deadline = highest dispatch priority.
/// Uses monotonic clock (Environment.TickCount64) to avoid NTP drift.
/// </summary>
internal struct TimeCriticalPiece : IComparable<TimeCriticalPiece>
{
    public int PieceIndex { get; init; }
    public long DeadlineTicks { get; set; }
    public long FirstRequestedTicks { get; init; }
    public int PeerCount { get; set; }
    public bool AlertWhenAvailable { get; set; }

    public int CompareTo(TimeCriticalPiece other) => DeadlineTicks.CompareTo(other.DeadlineTicks);
}
