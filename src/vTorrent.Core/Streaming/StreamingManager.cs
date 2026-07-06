using System;
using System.Collections.Generic;

namespace vTorrent.Core.Streaming;

/// <summary>
/// Thread-safe deadline manager for streaming piece prioritization.
/// Uses binary-search insert to maintain deadline-sorted order.
/// </summary>
internal sealed class StreamingManager : IStreamingManager
{
    private readonly List<TimeCriticalPiece> _pieces = new();
    private readonly object _lock = new();
    private readonly int _totalPieces;

    public event Action<int>? PieceAvailable;

    public StreamingManager(int totalPieces)
    {
        if (totalPieces <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalPieces), "Total pieces must be positive.");
        _totalPieces = totalPieces;
    }

    public bool HasDeadlines
    {
        get
        {
            lock (_lock)
                return _pieces.Count > 0;
        }
    }

    public bool SetPieceDeadline(int pieceIndex, int deadlineMs, bool alertWhenAvailable = false)
    {
        ValidatePieceIndex(pieceIndex);

        long now = Environment.TickCount64;
        long deadlineTicks = now + deadlineMs;

        lock (_lock)
        {
            // Check if piece already has a deadline
            for (int i = 0; i < _pieces.Count; i++)
            {
                if (_pieces[i].PieceIndex == pieceIndex)
                {
                    // Update existing entry: remove, re-insert at correct position
                    var existing = _pieces[i];
                    _pieces.RemoveAt(i);

                    var updated = new TimeCriticalPiece
                    {
                        PieceIndex = pieceIndex,
                        DeadlineTicks = deadlineTicks,
                        FirstRequestedTicks = existing.FirstRequestedTicks,
                        PeerCount = existing.PeerCount,
                        AlertWhenAvailable = alertWhenAvailable,
                    };
                    InsertSorted(updated);
                    return false; // Not the first deadline
                }
            }

            // New entry
            var piece = new TimeCriticalPiece
            {
                PieceIndex = pieceIndex,
                DeadlineTicks = deadlineTicks,
                FirstRequestedTicks = now,
                PeerCount = 0,
                AlertWhenAvailable = alertWhenAvailable,
            };

            bool isFirst = _pieces.Count == 0;
            InsertSorted(piece);
            return isFirst;
        }
    }

    public void ResetPieceDeadline(int pieceIndex)
    {
        ValidatePieceIndex(pieceIndex);

        lock (_lock)
        {
            for (int i = 0; i < _pieces.Count; i++)
            {
                if (_pieces[i].PieceIndex == pieceIndex)
                {
                    _pieces.RemoveAt(i);
                    return;
                }
            }
        }
    }

    public void ClearPieceDeadlines()
    {
        lock (_lock)
            _pieces.Clear();
    }

    public IReadOnlyList<TimeCriticalPiece> GetTimeCriticalPieces(Func<int, bool> isCompleted)
    {
        ArgumentNullException.ThrowIfNull(isCompleted);

        lock (_lock)
        {
            var result = new List<TimeCriticalPiece>(_pieces.Count);
            for (int i = 0; i < _pieces.Count; i++)
            {
                if (!isCompleted(_pieces[i].PieceIndex))
                    result.Add(_pieces[i]);
            }
            return result;
        }
    }

    public bool OnPieceCompleted(int pieceIndex)
    {
        ValidatePieceIndex(pieceIndex);

        bool shouldAlert = false;

        lock (_lock)
        {
            for (int i = 0; i < _pieces.Count; i++)
            {
                if (_pieces[i].PieceIndex == pieceIndex)
                {
                    shouldAlert = _pieces[i].AlertWhenAvailable;
                    _pieces.RemoveAt(i);

                    if (shouldAlert)
                        PieceAvailable?.Invoke(pieceIndex);

                    return true;
                }
            }
        }

        return false;
    }

    public void IncrementPeerCount(int pieceIndex)
    {
        ValidatePieceIndex(pieceIndex);

        lock (_lock)
        {
            for (int i = 0; i < _pieces.Count; i++)
            {
                if (_pieces[i].PieceIndex == pieceIndex)
                {
                    var p = _pieces[i];
                    p.PeerCount++;
                    _pieces[i] = p;
                    return;
                }
            }
        }
    }

    private void InsertSorted(TimeCriticalPiece piece)
    {
        int index = _pieces.BinarySearch(piece);
        if (index < 0)
            index = ~index;
        _pieces.Insert(index, piece);
    }

    private void ValidatePieceIndex(int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= _totalPieces)
            throw new ArgumentOutOfRangeException(nameof(pieceIndex),
                $"Piece index {pieceIndex} is out of range [0, {_totalPieces}).");
    }
}
