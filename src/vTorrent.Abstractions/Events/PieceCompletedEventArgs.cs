using System;

namespace vTorrent.Abstractions.Events;

public class PieceCompletedEventArgs : EventArgs
{
    public int PieceIndex { get; }
    public int CompletedPieces { get; }
    public int TotalPieces { get; }

    public PieceCompletedEventArgs(int pieceIndex, int completedPieces, int totalPieces)
    {
        PieceIndex = pieceIndex;
        CompletedPieces = completedPieces;
        TotalPieces = totalPieces;
    }
}