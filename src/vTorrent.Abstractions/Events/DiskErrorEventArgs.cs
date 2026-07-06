using System;

namespace vTorrent.Abstractions.Events;

public class DiskErrorEventArgs : EventArgs
{
    public int PieceIndex { get; }
    public string? ErrorMessage { get; }

    public DiskErrorEventArgs(int pieceIndex, string? errorMessage)
    {
        PieceIndex = pieceIndex;
        ErrorMessage = errorMessage;
    }
}
