using System;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// Identifies a block within a piece. Equality is based on (PieceIndex, Begin) only.
/// Length is response metadata — peers may send different lengths than requested.
/// Using Length in equality caused pending request lookup failures and pipeline stalls.
/// </summary>
public readonly record struct BlockRequest(int PieceIndex, int Begin, int Length)
{
    public bool Equals(BlockRequest other) =>
        PieceIndex == other.PieceIndex && Begin == other.Begin;

    public override int GetHashCode() =>
        HashCode.Combine(PieceIndex, Begin);
}
