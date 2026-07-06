using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;

namespace vTorrent.Core.Download;

/// <summary>
/// In-memory write cache for piece data. Owns byte buffers rented from ArrayPool.
/// Accepts blocks as they arrive, provides assembled piece data for hashing,
/// and returns buffers on completion/failure/eviction.
/// libtorrent equivalent: block_cache / disk_buffer_pool.
/// </summary>
public class DiskWriteCache
{
    private readonly ConcurrentDictionary<int, CachedPiece> _pieces = new();
    private long _totalCachedBytes;
    private readonly long _memoryCap;

    public long TotalCachedBytes => Interlocked.Read(ref _totalCachedBytes);

    public DiskWriteCache(long memoryCap = 64 * 1024 * 1024)
    {
        _memoryCap = memoryCap;
    }

    /// <summary>
    /// Adds a block to the cache. Allocates piece buffer on first block.
    /// Returns false if eviction was needed but failed (all pieces protected).
    /// Thread-safe: uses TryGetValue + TryAdd to avoid double-rent race in GetOrAdd factory.
    /// </summary>
    public bool AddBlock(int pieceIndex, long pieceSize, int offset, byte[] blockData, int length)
    {
        if (!_pieces.TryGetValue(pieceIndex, out var cached))
        {
            // Allocate new buffer — only one thread's TryAdd will succeed
            var buffer = ArrayPool<byte>.Shared.Rent((int)pieceSize);
            var newCached = new CachedPiece(pieceIndex, buffer, pieceSize);

            if (_pieces.TryAdd(pieceIndex, newCached))
            {
                Interlocked.Add(ref _totalCachedBytes, pieceSize);
                cached = newCached;
                // Protect from LRU eviction while in-progress.
                // PieceBlockTracker tracks received blocks independently from the cache.
                // If an in-progress piece is evicted and re-created, the tracker still
                // shows early blocks as "received" but their data is gone — the new
                // ArrayPool buffer has junk. Hash verification then reads junk + real
                // data → failure. CompletePieceAsync's ProtectPiece call is too late
                // (only runs when the LAST block arrives). Protect from creation.
                // ReleasePiece/DiscardPiece already remove from cache on completion/failure.
                cached.IsProtected = true;
            }
            else
            {
                // Another thread won the race — return our unused buffer
                ArrayPool<byte>.Shared.Return(buffer);
                cached = _pieces[pieceIndex];
            }
        }

        // Validate offset is within piece bounds. If the block extends past
        // pieceSize (common: peers send full 16KB blocks even for the last block
        // of the last piece), truncate to fit. The extra bytes are harmless —
        // hash verification slices to exact pieceSize anyway.
        // Legacy code had no bounds check; ArrayPool buffers are always >= pieceSize
        // so Buffer.BlockCopy never overflows the actual array.
        if (offset < 0 || offset >= (int)cached.PieceSize)
            return false;

        int safeLength = Math.Min(length, (int)cached.PieceSize - offset);
        if (safeLength <= 0)
            return false;

        // Copy block data into piece buffer
        Buffer.BlockCopy(blockData, 0, cached.Buffer, offset, safeLength);
        cached.BlocksWritten++;
        cached.LastAccessTicks = Environment.TickCount64;

        // Check memory cap and evict if needed
        if (Interlocked.Read(ref _totalCachedBytes) > _memoryCap)
            EvictLRU();

        return true;
    }

    /// <summary>
    /// Returns the piece's byte buffer, or null if not cached.
    /// Updates last-access time for LRU tracking.
    /// </summary>
    public byte[]? GetPieceData(int pieceIndex)
    {
        if (_pieces.TryGetValue(pieceIndex, out var cached))
        {
            cached.LastAccessTicks = Environment.TickCount64;
            return cached.Buffer;
        }
        return null;
    }

    /// <summary>
    /// Returns true if the piece has cached data.
    /// </summary>
    public bool HasPieceData(int pieceIndex) => _pieces.ContainsKey(pieceIndex);

    /// <summary>
    /// Marks a piece as protected (Finished state — awaiting hash/write).
    /// Protected pieces are NOT evicted by LRU.
    /// </summary>
    public void ProtectPiece(int pieceIndex)
    {
        if (_pieces.TryGetValue(pieceIndex, out var cached))
            cached.IsProtected = true;
    }

    /// <summary>
    /// Removes protection from a piece (e.g., when hash/write completes).
    /// </summary>
    public void UnprotectPiece(int pieceIndex)
    {
        if (_pieces.TryGetValue(pieceIndex, out var cached))
            cached.IsProtected = false;
    }

    /// <summary>
    /// Releases a piece's buffer back to ArrayPool and removes from cache.
    /// Called after successful disk write.
    /// </summary>
    public void ReleasePiece(int pieceIndex)
    {
        if (_pieces.TryRemove(pieceIndex, out var cached))
        {
            Interlocked.Add(ref _totalCachedBytes, -cached.PieceSize);
            ArrayPool<byte>.Shared.Return(cached.Buffer);
        }
    }

    /// <summary>
    /// Discards a piece's cached data. Same as ReleasePiece.
    /// Called on hash failure.
    /// </summary>
    public void DiscardPiece(int pieceIndex) => ReleasePiece(pieceIndex);

    /// <summary>
    /// Releases all cached buffers. Called on torrent stop/dispose.
    /// </summary>
    public void DisposeAll()
    {
        foreach (var kvp in _pieces)
        {
            if (_pieces.TryRemove(kvp.Key, out var cached))
            {
                Interlocked.Add(ref _totalCachedBytes, -cached.PieceSize);
                ArrayPool<byte>.Shared.Return(cached.Buffer);
            }
        }
    }

    private void EvictLRU()
    {
        while (Interlocked.Read(ref _totalCachedBytes) > _memoryCap)
        {
            int lruPiece = -1;
            long lruTicks = long.MaxValue;

            foreach (var kvp in _pieces)
            {
                if (kvp.Value.IsProtected) continue; // Skip Finished pieces
                if (kvp.Value.LastAccessTicks < lruTicks)
                {
                    lruTicks = kvp.Value.LastAccessTicks;
                    lruPiece = kvp.Key;
                }
            }

            if (lruPiece < 0) break; // All pieces protected — can't evict
            ReleasePiece(lruPiece);
        }
    }

    private class CachedPiece
    {
        public readonly int PieceIndex;
        public readonly byte[] Buffer;
        public readonly long PieceSize;
        public long LastAccessTicks;
        public int BlocksWritten;
        public volatile bool IsProtected;

        public CachedPiece(int pieceIndex, byte[] buffer, long pieceSize)
        {
            PieceIndex = pieceIndex;
            Buffer = buffer;
            PieceSize = pieceSize;
            LastAccessTicks = Environment.TickCount64;
        }
    }
}
