using System;

namespace vTorrent.Abstractions.Interfaces.Engine;

/// <summary>
/// O(1) piece picker using libtorrent-style swap-based bucket sort.
/// Priority = availability * 2 + (isPartial ? 0 : 1).
/// </summary>
public interface IBucketPiecePicker
{
    /// <summary>Pick the highest-priority piece the peer has. Returns null if none available.</summary>
    int? PickPiece(Func<int, bool> peerHasPiece, bool sequential = false,
        int extentPieceLength = 0, int extentSize = 0);

    /// <summary>O(1) availability update when a peer announces a piece.</summary>
    void IncrementAvailability(int pieceIndex);

    /// <summary>O(1) availability update when a peer disconnects.</summary>
    void DecrementAvailability(int pieceIndex);

    /// <summary>Bulk availability update from a bitfield (peer connect).</summary>
    void ApplyBitfield(byte[] bitfield, int pieceCount, int delta);

    /// <summary>Mark piece as in-progress (partial priority boost).</summary>
    void MarkInProgress(int pieceIndex);

    /// <summary>Mark piece as completed (remove from picker).</summary>
    void MarkCompleted(int pieceIndex);

    /// <summary>Mark piece as available again (hash failure).</summary>
    void MarkAvailable(int pieceIndex);

    /// <summary>Reset an in-progress piece back to available (disk write failure retry).</summary>
    void MarkNotStarted(int pieceIndex);

    /// <summary>O(1) seed counter adjustment (avoids N swaps).</summary>
    void OnSeedJoined();
    void OnSeedLeft();

    /// <summary>Number of pickable pieces remaining.</summary>
    int AvailablePieceCount { get; }

    /// <summary>Check if a piece is in Completed state (for reconciliation).</summary>
    bool IsPieceCompleted(int pieceIndex);

    /// <summary>
    /// Pick a piece in reverse priority order (highest availability first).
    /// Used for snubbed peers to concentrate them on common pieces.
    /// </summary>
    int? PickPieceReverse(Func<int, bool> peerHasPiece);

    /// <summary>Mark piece as finished (all blocks received, awaiting hash + write).</summary>
    void MarkFinished(int pieceIndex);

    /// <summary>Restore a finished piece back to available (hash failure).</summary>
    void RestorePiece(int pieceIndex);

    /// <summary>Get piece state: 0=Available, 1=InProgress, 2=Completed, 3=Finished.</summary>
    int GetPieceState(int pieceIndex);

    /// <summary>Get tracked availability count for a piece.</summary>
    int GetPieceAvailability(int pieceIndex);
}
