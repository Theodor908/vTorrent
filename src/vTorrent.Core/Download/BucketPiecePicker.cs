using System;
using vTorrent.Core.Interfaces;
using vTorrent.Abstractions.Interfaces.Engine;

namespace vTorrent.Core.Download;

/// <summary>
/// O(1) piece picker using libtorrent-style swap-based bucket sort.
///
/// Data structures:
/// - _pieces[]: all piece indices sorted by priority; equal-priority shuffled
/// - _pieceMap[]: indexed by piece index; stores availability, position, state
/// - _bucketBoundaries[]: indices into _pieces marking priority bucket starts
///
/// Priority formula: availability * 2 + (isPartial ? 0 : 1)
/// Lower priority value = picked first (rarest-first, partial-preferred).
/// </summary>
public class BucketPiecePicker : IBucketPiecePicker
{
    private enum PieceState : byte { Available, InProgress, Completed, Finished }

    private struct PieceEntry
    {
        public int Availability;
        public int PositionInPieces; // index into _pieces array
        public PieceState State;
    }

    // Thread safety: all public methods acquire this lock.
    // libtorrent runs its picker on a single session thread; we use locking
    // because vTorrent's message handlers run on per-peer receive threads.
    private readonly object _lock = new();

    private readonly int[] _pieces;       // piece indices sorted by priority
    private readonly PieceEntry[] _pieceMap; // indexed by piece index
    private int _activeCount;             // number of non-completed pieces in _pieces
    private readonly int _pieceCount;

    // Bucket boundaries: _bucketBoundaries[priority] = first index in _pieces for that priority
    // Max priority = (maxAvailability * 2 + 1). We resize dynamically.
    private int[] _bucketBoundaries;
    private int _maxPriority;

    // Seed optimization: lazy rebuild instead of N swaps
    private int _seedCount;
    private bool _dirty;

    private bool _prioritizePartialPieces = false;

    // Extent affinity: tracks the last extent start we picked from for disk I/O locality.
    private int _lastPickedExtentStart = -1;

    public int AvailablePieceCount { get { lock (_lock) return _activeCount; } }

    public void SetPrioritizePartialPieces(bool value) => _prioritizePartialPieces = value;

    public BucketPiecePicker(int pieceCount)
    {
        _pieceCount = pieceCount;
        _pieces = new int[pieceCount];
        _pieceMap = new PieceEntry[pieceCount];
        _activeCount = pieceCount;

        // All pieces start with availability 0 — they're in the "unpickable" zone.
        // We use a single bucket for priority 0 (the "no availability" sentinel).
        _maxPriority = 1;
        _bucketBoundaries = new int[2]; // [0] = start of priority 0, [1] = end sentinel

        for (int i = 0; i < pieceCount; i++)
        {
            _pieces[i] = i;
            _pieceMap[i] = new PieceEntry
            {
                Availability = 0,
                PositionInPieces = i,
                State = PieceState.Available
            };
        }

        // Single bucket: all at "no availability" priority
        _bucketBoundaries[0] = 0;
        _bucketBoundaries[1] = pieceCount;
    }

    public int? PickPiece(Func<int, bool> peerHasPiece, bool sequential = false,
        int extentPieceLength = 0, int extentSize = 0)
    {
        lock (_lock)
        {
            if (_dirty) Rebuild();

            if (sequential)
                return PickSequential(peerHasPiece);

            int? piece = null;

            // Extent affinity: if enabled and we have a last extent, try to pick from it first.
            if (extentPieceLength > 0 && extentSize > 0 && _lastPickedExtentStart >= 0)
            {
                int extentPieceCount = Math.Max(1, extentSize / extentPieceLength);
                int extentEnd = Math.Min(_lastPickedExtentStart + extentPieceCount, _pieceCount);
                for (int p = _lastPickedExtentStart; p < extentEnd; p++)
                {
                    if (_pieceMap[p].Availability == 0) continue;
                    if (_pieceMap[p].State is PieceState.Completed or PieceState.Finished or PieceState.InProgress) continue;
                    if (peerHasPiece(p))
                    {
                        piece = p;
                        break;
                    }
                }
            }

            if (piece == null)
            {
                // Iterate buckets from lowest priority value (rarest, partial-preferred) to highest.
                // Skip priority 0 bucket — those are pieces with availability == 0 (unpickable)
                // unless they got moved by IncrementAvailability. We check availability inside the loop.
                for (int p = 0; p < _maxPriority; p++)
                {
                    int start = _bucketBoundaries[p];
                    int end = (p + 1 < _bucketBoundaries.Length) ? _bucketBoundaries[p + 1] : _activeCount;
                    end = Math.Min(end, _activeCount);

                    for (int i = start; i < end; i++)
                    {
                        int candidate = _pieces[i];
                        if (_pieceMap[candidate].Availability == 0) continue; // unpickable
                        if (_pieceMap[candidate].State is PieceState.Completed or PieceState.Finished) continue;
                        if (peerHasPiece(candidate))
                        {
                            piece = candidate;
                            break;
                        }
                    }

                    if (piece != null) break;
                }

                // Fallback: if rarest-first found nothing, try any piece the peer has.
                // Handles all-leecher swarms where availability is universally 0.
                if (piece == null)
                {
                    for (int i = 0; i < _activeCount; i++)
                    {
                        int candidate = _pieces[i];
                        if (_pieceMap[candidate].State is PieceState.Completed or PieceState.Finished) continue;
                        if (peerHasPiece(candidate))
                        {
                            piece = candidate;
                            break;
                        }
                    }
                }
            }

            // Update last extent when extent affinity is enabled
            if (extentPieceLength > 0 && extentSize > 0 && piece.HasValue)
            {
                int extentPieceCount = Math.Max(1, extentSize / extentPieceLength);
                _lastPickedExtentStart = (piece.Value / extentPieceCount) * extentPieceCount;
            }

            return piece;
        }
    }

    /// <summary>
    /// Picks a piece in reverse priority order (highest availability first).
    /// Used for snubbed peers — concentrates slow peers on common pieces
    /// so they don't block completion of rare pieces fast peers are downloading.
    /// libtorrent equivalent: piece_picker::pick_pieces with reverse flag.
    /// </summary>
    public int? PickPieceReverse(Func<int, bool> peerHasPiece)
    {
        lock (_lock)
        {
            if (_dirty) Rebuild();

            // Iterate buckets from highest priority (most available) to lowest (rarest)
            for (int p = _maxPriority - 1; p >= 0; p--)
            {
                int start = _bucketBoundaries[p];
                int end = (p + 1 < _bucketBoundaries.Length) ? _bucketBoundaries[p + 1] : _activeCount;
                end = Math.Min(end, _activeCount);

                for (int i = start; i < end; i++)
                {
                    int piece = _pieces[i];
                    if (_pieceMap[piece].Availability == 0) continue;
                    if (_pieceMap[piece].State is PieceState.Completed or PieceState.Finished) continue;
                    if (peerHasPiece(piece))
                        return piece;
                }
            }

            // Fallback: all-leecher swarm where availability is universally 0.
            // Iterate in reverse index order to still separate from normal picks.
            for (int i = _activeCount - 1; i >= 0; i--)
            {
                int piece = _pieces[i];
                if (_pieceMap[piece].State is PieceState.Completed or PieceState.Finished) continue;
                if (peerHasPiece(piece))
                    return piece;
            }

            return null;
        }
    }

    private int? PickSequential(Func<int, bool> peerHasPiece)
    {
        for (int i = 0; i < _pieceCount; i++)
        {
            ref var entry = ref _pieceMap[i];
            if (entry.State is PieceState.Completed or PieceState.Finished) continue;
            if (entry.Availability == 0) continue;
            if (peerHasPiece(i))
                return i;
        }

        // Fallback: ignore availability for all-leecher swarms
        for (int i = 0; i < _pieceCount; i++)
        {
            ref var entry = ref _pieceMap[i];
            if (entry.State is PieceState.Completed or PieceState.Finished) continue;
            if (peerHasPiece(i))
                return i;
        }
        return null;
    }

    public void IncrementAvailability(int pieceIndex)
    {
        lock (_lock)
        {
            if ((uint)pieceIndex >= (uint)_pieceCount) return;
            ref var entry = ref _pieceMap[pieceIndex];
            if (entry.State == PieceState.Completed || entry.State == PieceState.Finished) return;

            int oldPriority = GetPriority(ref entry);
            entry.Availability++;
            int newPriority = GetPriority(ref entry);

            EnsureBucketCapacity(newPriority);
            MovePiece(pieceIndex, oldPriority, newPriority);
        }
    }

    public void DecrementAvailability(int pieceIndex)
    {
        lock (_lock)
        {
            if ((uint)pieceIndex >= (uint)_pieceCount) return;
            ref var entry = ref _pieceMap[pieceIndex];
            if (entry.State == PieceState.Completed || entry.State == PieceState.Finished) return;
            if (entry.Availability <= 0) return;

            int oldPriority = GetPriority(ref entry);
            entry.Availability--;
            int newPriority = GetPriority(ref entry);

            MovePiece(pieceIndex, oldPriority, newPriority);
        }
    }

    public void ApplyBitfield(byte[] bitfield, int pieceCount, int delta)
    {
        lock (_lock)
        {
            int count = Math.Min(pieceCount, _pieceCount);
            for (int i = 0; i < count; i++)
            {
                int byteIdx = i / 8;
                int bitIdx = 7 - (i % 8); // BitTorrent: MSB = lowest piece index
                if (byteIdx < bitfield.Length && (bitfield[byteIdx] & (1 << bitIdx)) != 0)
                {
                    if (delta > 0) IncrementAvailabilityUnsafe(i);
                    else DecrementAvailabilityUnsafe(i);
                }
            }
        }
    }

    public void MarkInProgress(int pieceIndex)
    {
        lock (_lock)
        {
            if ((uint)pieceIndex >= (uint)_pieceCount) return;
            ref var entry = ref _pieceMap[pieceIndex];
            if (entry.State != PieceState.Available) return;

            int oldPriority = GetPriority(ref entry);
            entry.State = PieceState.InProgress;
            int newPriority = GetPriority(ref entry);

            if (oldPriority != newPriority)
                MovePiece(pieceIndex, oldPriority, newPriority);
        }
    }

    public void MarkCompleted(int pieceIndex)
    {
        lock (_lock)
        {
            if ((uint)pieceIndex >= (uint)_pieceCount) return;
            ref var entry = ref _pieceMap[pieceIndex];
            if (entry.State == PieceState.Completed) return;

            // Move piece to the last bucket first so boundaries stay consistent,
            // then swap with the last active piece and shrink the active range.
            int oldPriority = GetPriority(ref entry);
            int lastBucketPriority = Math.Max(_maxPriority - 1, 0);
            EnsureBucketCapacity(lastBucketPriority);
            MovePiece(pieceIndex, oldPriority, lastBucketPriority);

            int pos = entry.PositionInPieces;
            if (pos < _activeCount - 1)
            {
                int lastPiece = _pieces[_activeCount - 1];
                SwapPositions(pieceIndex, lastPiece);
            }
            _activeCount--;
            entry.State = PieceState.Completed;
        }
    }

    public void MarkNotStarted(int pieceIndex)
    {
        lock (_lock)
        {
            if ((uint)pieceIndex >= (uint)_pieceCount) return;
            ref var entry = ref _pieceMap[pieceIndex];
            if (entry.State != PieceState.InProgress) return;

            int oldPriority = GetPriority(ref entry);
            entry.State = PieceState.Available;
            int newPriority = GetPriority(ref entry);

            if (oldPriority != newPriority)
                MovePiece(pieceIndex, oldPriority, newPriority);
        }
    }

    /// <summary>
    /// Transition InProgress → Finished (all blocks received, awaiting hash + disk write).
    /// Piece remains in active set but is not pickable.
    /// libtorrent equivalent: piece_picker::mark_as_finished.
    /// </summary>
    public void MarkFinished(int pieceIndex)
    {
        lock (_lock)
        {
            if ((uint)pieceIndex >= (uint)_pieceCount) return;
            ref var entry = ref _pieceMap[pieceIndex];
            if (entry.State != PieceState.InProgress) return;

            // Priority doesn't change (Finished treated same as InProgress in GetPriority)
            entry.State = PieceState.Finished;
        }
    }

    /// <summary>
    /// Transition Finished → Available (hash failure, piece must be re-downloaded).
    /// libtorrent equivalent: piece_picker::restore_piece.
    /// </summary>
    public void RestorePiece(int pieceIndex)
    {
        lock (_lock)
        {
            if ((uint)pieceIndex >= (uint)_pieceCount) return;
            ref var entry = ref _pieceMap[pieceIndex];
            if (entry.State != PieceState.Finished) return;

            int oldPriority = GetPriority(ref entry);
            entry.State = PieceState.Available;
            int newPriority = GetPriority(ref entry);

            if (oldPriority != newPriority)
                MovePiece(pieceIndex, oldPriority, newPriority);
        }
    }

    /// <summary>
    /// Returns the current state of a piece as an int for external inspection.
    /// 0=Available, 1=InProgress, 2=Completed, 3=Finished.
    /// </summary>
    public int GetPieceState(int pieceIndex)
    {
        lock (_lock)
        {
            if ((uint)pieceIndex >= (uint)_pieceCount) return -1;
            return (int)_pieceMap[pieceIndex].State;
        }
    }

    /// <summary>
    /// Returns the picker's tracked availability count for a piece.
    /// </summary>
    public int GetPieceAvailability(int pieceIndex)
    {
        lock (_lock)
        {
            if ((uint)pieceIndex >= (uint)_pieceCount) return -1;
            return _pieceMap[pieceIndex].Availability;
        }
    }

    public void MarkAvailable(int pieceIndex)
    {
        lock (_lock)
        {
            if ((uint)pieceIndex >= (uint)_pieceCount) return;
            ref var entry = ref _pieceMap[pieceIndex];
            if (entry.State != PieceState.Completed) return;

            entry.State = PieceState.Available;

            // Explicitly place piece at the new last active slot
            int newPos = _activeCount;
            int currentOccupant = _pieces[newPos];
            _pieces[newPos] = pieceIndex;
            _pieces[entry.PositionInPieces] = currentOccupant;
            _pieceMap[currentOccupant].PositionInPieces = entry.PositionInPieces;
            entry.PositionInPieces = newPos;

            _activeCount++;
            _dirty = true; // Rebuild to fix bucket positions
        }
    }

    public void OnSeedJoined()
    {
        lock (_lock)
        {
            _seedCount++;
            _dirty = true;
        }
    }

    public void OnSeedLeft()
    {
        lock (_lock)
        {
            _seedCount = Math.Max(0, _seedCount - 1);
            _dirty = true;
        }
    }

    /// <summary>
    /// Check if a piece is in Completed state. Thread-safe.
    /// Used by reconciliation to detect picker/bitfield drift.
    /// </summary>
    public bool IsPieceCompleted(int pieceIndex)
    {
        lock (_lock)
        {
            if ((uint)pieceIndex >= (uint)_pieceCount) return false;
            return _pieceMap[pieceIndex].State == PieceState.Completed;
        }
    }

    // --- Internal helpers (caller must hold _lock) ---

    /// <summary>Lock-free IncrementAvailability for use inside already-locked methods.</summary>
    private void IncrementAvailabilityUnsafe(int pieceIndex)
    {
        if ((uint)pieceIndex >= (uint)_pieceCount) return;
        ref var entry = ref _pieceMap[pieceIndex];
        if (entry.State == PieceState.Completed || entry.State == PieceState.Finished) return;

        int oldPriority = GetPriority(ref entry);
        entry.Availability++;
        int newPriority = GetPriority(ref entry);

        EnsureBucketCapacity(newPriority);
        MovePiece(pieceIndex, oldPriority, newPriority);
    }

    /// <summary>Lock-free DecrementAvailability for use inside already-locked methods.</summary>
    private void DecrementAvailabilityUnsafe(int pieceIndex)
    {
        if ((uint)pieceIndex >= (uint)_pieceCount) return;
        ref var entry = ref _pieceMap[pieceIndex];
        if (entry.State == PieceState.Completed || entry.State == PieceState.Finished) return;
        if (entry.Availability <= 0) return;

        int oldPriority = GetPriority(ref entry);
        entry.Availability--;
        int newPriority = GetPriority(ref entry);

        MovePiece(pieceIndex, oldPriority, newPriority);
    }

    private int GetPriority(ref PieceEntry entry)
    {
        // Availability 0 = max priority (unpickable, sorted to end)
        if (entry.Availability == 0) return _maxPriority;

        // _seedCount is NOT included here — it only applies during Rebuild().
        // This avoids bucket corruption when IncrementAvailability is called
        // after OnSeedJoined but before the next PickPiece triggers Rebuild.
        bool isPartial = _prioritizePartialPieces && entry.State is PieceState.InProgress or PieceState.Finished;
        return entry.Availability * 2 + (isPartial ? 0 : 1);
    }

    /// <summary>
    /// Priority calculation used only during Rebuild, which includes _seedCount.
    /// </summary>
    private int GetRebuildPriority(ref PieceEntry entry)
    {
        if (entry.Availability == 0) return _maxPriority;

        int effectiveAvailability = entry.Availability + _seedCount;
        bool isPartial = _prioritizePartialPieces && entry.State is PieceState.InProgress or PieceState.Finished;
        return effectiveAvailability * 2 + (isPartial ? 0 : 1);
    }

    private void EnsureBucketCapacity(int priority)
    {
        if (priority >= _bucketBoundaries.Length)
        {
            int newSize = Math.Max(priority + 2, _bucketBoundaries.Length * 2);
            var newBounds = new int[newSize];
            Array.Copy(_bucketBoundaries, newBounds, _bucketBoundaries.Length);
            // New buckets are empty — fill with _activeCount
            for (int i = _bucketBoundaries.Length; i < newSize; i++)
                newBounds[i] = _activeCount;
            _bucketBoundaries = newBounds;
            _maxPriority = Math.Max(_maxPriority, priority + 1);
        }
        if (priority >= _maxPriority)
            _maxPriority = priority + 1;
    }

    private void MovePiece(int pieceIndex, int oldPriority, int newPriority)
    {
        if (oldPriority == newPriority) return;

        ref var entry = ref _pieceMap[pieceIndex];
        int pos = entry.PositionInPieces;

        if (newPriority < oldPriority)
        {
            // Moving to higher priority (lower index) — swap toward front
            for (int p = oldPriority; p > newPriority; p--)
            {
                int boundaryIdx = Math.Min(p, _bucketBoundaries.Length - 1);
                if (boundaryIdx <= 0) break;
                int boundary = _bucketBoundaries[boundaryIdx];
                if (pos >= boundary)
                {
                    // Swap with first element of this bucket
                    int swapPiece = _pieces[boundary];
                    SwapPositions(pieceIndex, swapPiece);
                    _bucketBoundaries[boundaryIdx]++;
                    pos = entry.PositionInPieces;
                }
            }
        }
        else
        {
            // Moving to lower priority (higher index) — swap toward back
            for (int p = oldPriority + 1; p <= newPriority; p++)
            {
                int boundaryIdx = Math.Min(p, _bucketBoundaries.Length - 1);
                int boundary = _bucketBoundaries[boundaryIdx];
                if (boundary > 0 && pos < boundary)
                {
                    // Swap with last element of previous bucket
                    int swapPiece = _pieces[boundary - 1];
                    SwapPositions(pieceIndex, swapPiece);
                    _bucketBoundaries[boundaryIdx]--;
                    pos = entry.PositionInPieces;
                }
            }
        }
    }

    private void SwapPositions(int pieceA, int pieceB)
    {
        if (pieceA == pieceB) return;
        ref var entryA = ref _pieceMap[pieceA];
        ref var entryB = ref _pieceMap[pieceB];

        (_pieces[entryA.PositionInPieces], _pieces[entryB.PositionInPieces]) =
            (_pieces[entryB.PositionInPieces], _pieces[entryA.PositionInPieces]);

        (entryA.PositionInPieces, entryB.PositionInPieces) =
            (entryB.PositionInPieces, entryA.PositionInPieces);
    }

    private void Rebuild()
    {
        _dirty = false;

        // Recalculate all priorities and sort
        var priorities = new int[_activeCount];
        for (int i = 0; i < _activeCount; i++)
        {
            int piece = _pieces[i];
            priorities[i] = GetRebuildPriority(ref _pieceMap[piece]);
        }

        // Sort _pieces[0.._activeCount] by priority
        Array.Sort(priorities, _pieces, 0, _activeCount);

        // Rebuild pieceMap positions and bucket boundaries
        int maxP = 0;
        for (int i = 0; i < _activeCount; i++)
        {
            _pieceMap[_pieces[i]].PositionInPieces = i;
            maxP = Math.Max(maxP, priorities[i]);
        }

        _maxPriority = maxP + 1;
        EnsureBucketCapacity(_maxPriority);

        // Rebuild boundaries
        Array.Fill(_bucketBoundaries, _activeCount);
        if (_activeCount > 0)
        {
            _bucketBoundaries[priorities[0]] = 0;
            for (int i = 1; i < _activeCount; i++)
            {
                if (priorities[i] != priorities[i - 1])
                    _bucketBoundaries[priorities[i]] = i;
            }
        }
    }
}
