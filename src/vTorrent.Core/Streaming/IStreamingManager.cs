using System;
using System.Collections.Generic;

namespace vTorrent.Core.Streaming;

internal interface IStreamingManager
{
    bool SetPieceDeadline(int pieceIndex, int deadlineMs, bool alertWhenAvailable = false);
    void ResetPieceDeadline(int pieceIndex);
    void ClearPieceDeadlines();
    bool HasDeadlines { get; }
    IReadOnlyList<TimeCriticalPiece> GetTimeCriticalPieces(Func<int, bool> isCompleted);
    bool OnPieceCompleted(int pieceIndex);
    void IncrementPeerCount(int pieceIndex);
    event Action<int>? PieceAvailable;
}
