using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.PieceIO;

namespace vTorrent.Bench.Simulation;

/// <summary>
/// In-memory IPieceManager for benchmarking. No disk I/O — pieces are stored in a
/// ConcurrentDictionary and hashes are verified with real SHA-1.
/// </summary>
public sealed class MemoryPieceManager : IPieceManager
{
    private readonly TorrentInfo _torrentInfo;
    private readonly int _pieceCount;
    private readonly ConcurrentDictionary<int, byte[]> _pieces = new();

    // BitArray is NOT thread-safe — guard every access with _bitfieldLock (CLAUDE.md rule #5)
    private readonly BitArray _bitfield;
    private readonly object _bitfieldLock = new();
    private int _completedPieceCount;

    public MemoryPieceManager(TorrentInfo torrentInfo)
    {
        _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(torrentInfo));
        _pieceCount = torrentInfo.Pieces?.Count ?? 0;
        _bitfield = new BitArray(_pieceCount, false);
    }

    // -------------------------------------------------------------------------
    // IPieceManager — Write
    // -------------------------------------------------------------------------

    public Task<PieceWriteResult> WritePieceAsync(
        int pieceIndex,
        byte[] data,
        CancellationToken cancellationToken = default,
        bool skipVerification = false)
    {
        return Task.FromResult(WritePiece(pieceIndex, data, skipVerification));
    }

    public PieceWriteResult WritePiece(int pieceIndex, byte[] data)
        => WritePiece(pieceIndex, data, skipVerification: false);

    private PieceWriteResult WritePiece(int pieceIndex, byte[] data, bool skipVerification)
    {
        if (pieceIndex < 0 || pieceIndex >= _pieceCount)
            return PieceWriteResult.Failure(pieceIndex, PieceWriteError.InvalidPieceIndex,
                $"Piece index {pieceIndex} is out of range [0, {_pieceCount}).");

        if (data == null || data.Length == 0)
            return PieceWriteResult.Failure(pieceIndex, PieceWriteError.InvalidData,
                "Data is null or empty.");

        bool hashVerified = false;
        if (!skipVerification)
        {
            hashVerified = VerifyPiece(pieceIndex, data);
            if (!hashVerified)
                return PieceWriteResult.Failure(pieceIndex, PieceWriteError.HashMismatch,
                    $"SHA-1 hash mismatch for piece {pieceIndex}.");
        }
        else
        {
            hashVerified = true;
        }

        _pieces[pieceIndex] = data;

        lock (_bitfieldLock)
        {
            if (!_bitfield[pieceIndex])
            {
                _bitfield[pieceIndex] = true;
                Interlocked.Increment(ref _completedPieceCount);
            }
        }

        return PieceWriteResult.Success(pieceIndex, data.Length, hashVerified);
    }

    // -------------------------------------------------------------------------
    // IPieceManager — Read
    // -------------------------------------------------------------------------

    public Task<PieceReadResult> ReadPieceAsync(int pieceIndex, CancellationToken cancellationToken = default)
        => Task.FromResult(ReadPiece(pieceIndex));

    public PieceReadResult ReadPiece(int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= _pieceCount)
            return PieceReadResult.Failure(pieceIndex, PieceReadError.InvalidPieceIndex,
                $"Piece index {pieceIndex} is out of range [0, {_pieceCount}).");

        if (!_pieces.TryGetValue(pieceIndex, out var data))
            return PieceReadResult.Failure(pieceIndex, PieceReadError.FileNotFound,
                $"Piece {pieceIndex} has not been written yet.");

        return PieceReadResult.Success(pieceIndex, data, hashVerified: true);
    }

    public Task<PieceReadResult> ReadBlockAsync(int pieceIndex, int offset, int length, CancellationToken cancellationToken = default)
    {
        if (pieceIndex < 0 || pieceIndex >= _pieceCount)
            return Task.FromResult(PieceReadResult.Failure(pieceIndex, PieceReadError.InvalidPieceIndex,
                $"Piece index {pieceIndex} is out of range [0, {_pieceCount})."));

        if (!_pieces.TryGetValue(pieceIndex, out var data))
            return Task.FromResult(PieceReadResult.Failure(pieceIndex, PieceReadError.FileNotFound,
                $"Piece {pieceIndex} has not been written yet."));

        if (offset < 0 || length <= 0 || offset + length > data.Length)
            return Task.FromResult(PieceReadResult.Failure(pieceIndex, PieceReadError.InvalidPieceIndex,
                $"Block range [{offset}, {offset + length}) is out of piece bounds [0, {data.Length})."));

        var block = new byte[length];
        Buffer.BlockCopy(data, offset, block, 0, length);
        return Task.FromResult(PieceReadResult.Success(pieceIndex, block, hashVerified: true));
    }

    // -------------------------------------------------------------------------
    // IPieceManager — Verify
    // -------------------------------------------------------------------------

    public bool VerifyPiece(int pieceIndex, byte[] data)
    {
        if (_torrentInfo.Pieces == null)
            return false;

        ReadOnlySpan<byte> expected = _torrentInfo.Pieces.GetPieceHash(pieceIndex);

        Span<byte> actual = stackalloc byte[PieceHashes.HashSize];
        SHA1.HashData(data, actual);

        return expected.SequenceEqual(actual);
    }

    public bool HasValidPiece(int pieceIndex)
    {
        lock (_bitfieldLock)
        {
            return pieceIndex >= 0 && pieceIndex < _pieceCount && _bitfield[pieceIndex];
        }
    }

    // -------------------------------------------------------------------------
    // IPieceManager — Bitfield / completion state
    // -------------------------------------------------------------------------

    public BitArray GetBitfield()
    {
        lock (_bitfieldLock)
        {
            // Return a defensive copy so the caller cannot mutate internal state.
            return new BitArray(_bitfield);
        }
    }

    public int CompletedPieceCount => Volatile.Read(ref _completedPieceCount);

    public bool IsPieceComplete(int pieceIndex)
    {
        lock (_bitfieldLock)
        {
            return pieceIndex >= 0 && pieceIndex < _pieceCount && _bitfield[pieceIndex];
        }
    }

    public void SetPieceComplete(int pieceIndex, bool complete)
    {
        if (pieceIndex < 0 || pieceIndex >= _pieceCount)
            return;

        lock (_bitfieldLock)
        {
            bool current = _bitfield[pieceIndex];
            if (current == complete)
                return;

            _bitfield[pieceIndex] = complete;
            if (complete)
                Interlocked.Increment(ref _completedPieceCount);
            else
                Interlocked.Decrement(ref _completedPieceCount);
        }
    }

    public void InitializeFromResumeBitfield(BitArray resumeBitfield)
    {
        if (resumeBitfield == null)
            throw new ArgumentNullException(nameof(resumeBitfield));

        lock (_bitfieldLock)
        {
            int count = 0;
            int len = Math.Min(_pieceCount, resumeBitfield.Length);
            for (int i = 0; i < len; i++)
            {
                _bitfield[i] = resumeBitfield[i];
                if (resumeBitfield[i])
                    count++;
            }
            Volatile.Write(ref _completedPieceCount, count);
        }
    }

    // -------------------------------------------------------------------------
    // IPieceManager — Disk fence (no-op for in-memory backend)
    // -------------------------------------------------------------------------

    public bool IsFenced => false;

    public Task<bool> RaiseDiskFenceAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public void LowerDiskFence() { }

    public void UpdateBasePath(string newBasePath) { }

    // -------------------------------------------------------------------------
    // IPieceManager — File handle / access hint (no-op for in-memory backend)
    // -------------------------------------------------------------------------

    public ValueTask ReleaseWriteHandlesAsync() => ValueTask.CompletedTask;

    public void SetSkippedFiles(bool[]? skippedFiles) { }

    public Task FlushPieceAsync(int pieceIndex, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public void SetSequentialAccessHint(bool sequential) { }
}
