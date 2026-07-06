using System;
using System.Threading;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Download;

/// <summary>
/// Tracks block download state for a single piece. No byte data — state only.
/// Block states: 0=free, 1=requested, 2=received. CAS-based for thread safety.
/// libtorrent equivalent: piece_picker::downloading_piece block tracking.
/// </summary>
public class PieceBlockTracker
{
    private readonly int _pieceIndex;
    private readonly long _pieceSize;
    private readonly int _blockSize;
    private readonly int[] _blockState; // 0=free, 1=requested, 2=received
    private readonly string?[] _blockRequestedBy;
    private readonly int _blockCount;
    private int _blocksReceivedCount;
    private int _blocksWrittenCount;

    /// <summary>
    /// True when all blocks have been WRITTEN to the disk cache (not just marked received).
    /// Uses _blocksWrittenCount which is incremented after DiskWriteCache.AddBlock,
    /// ensuring all data is in the cache buffer before hash verification starts.
    /// Without this separation, a thread can see IsComplete=true and start hashing
    /// while another thread's AddBlock hasn't completed yet (race between CAS mark
    /// and Buffer.BlockCopy in concurrent multi-source delivery).
    /// libtorrent avoids this because incoming_piece() is synchronous in its reactor.
    /// </summary>
    public bool IsComplete => Volatile.Read(ref _blocksWrittenCount) >= _blockCount;
    public int BlockCount => _blockCount;
    public int PieceIndex => _pieceIndex;
    public long PieceSize => _pieceSize;

    public PieceBlockTracker(int pieceIndex, long pieceSize, int blockSize)
    {
        _pieceIndex = pieceIndex;
        _pieceSize = pieceSize;
        _blockSize = blockSize;
        _blockCount = (int)Math.Ceiling((double)pieceSize / blockSize);
        _blockState = new int[_blockCount];
        _blockRequestedBy = new string?[_blockCount];
    }

    /// <summary>
    /// Gets the next unrequested block and marks it as requested.
    /// CAS ensures exactly one caller wins each block.
    /// </summary>
    public BlockRequest? GetNextBlock(string peerId)
    {
        for (int i = 0; i < _blockCount; i++)
        {
            if (Interlocked.CompareExchange(ref _blockState[i], 1, 0) == 0)
            {
                _blockRequestedBy[i] = peerId;
                int begin = i * _blockSize;
                int length = (int)Math.Min(_blockSize, _pieceSize - begin);
                return new BlockRequest(_pieceIndex, begin, length);
            }
        }
        return null;
    }

    /// <summary>
    /// Gets the next unrequested block (no peer tracking).
    /// </summary>
    public BlockRequest? GetNextBlock()
    {
        for (int i = 0; i < _blockCount; i++)
        {
            if (Interlocked.CompareExchange(ref _blockState[i], 1, 0) == 0)
            {
                int begin = i * _blockSize;
                int length = (int)Math.Min(_blockSize, _pieceSize - begin);
                return new BlockRequest(_pieceIndex, begin, length);
            }
        }
        return null;
    }

    /// <summary>
    /// Transitions a block to received(2) from any non-received state.
    /// Accepts blocks from both requested(1) and free(0) states — the latter
    /// happens when endgame duplicate responses arrive after an orphan repair
    /// reset the block, or when a peer delivers a block whose pending entry
    /// was already removed by timeout/disconnect.
    /// Returns true if accepted, false if block was already received(2).
    /// </summary>
    public bool MarkBlockReceived(int begin)
    {
        int blockIndex = begin / _blockSize;
        if (blockIndex < 0 || blockIndex >= _blockCount) return false;

        // Try CAS from requested(1) → received(2) first (common case)
        if (Interlocked.CompareExchange(ref _blockState[blockIndex], 2, 1) == 1)
        {
            Interlocked.Increment(ref _blocksReceivedCount);
            return true;
        }

        // Also accept from free(0) → received(2) (endgame/orphan repair race)
        if (Interlocked.CompareExchange(ref _blockState[blockIndex], 2, 0) == 0)
        {
            Interlocked.Increment(ref _blocksReceivedCount);
            return true;
        }

        // Already received(2) — duplicate
        return false;
    }

    /// <summary>
    /// Signals that block data has been written to the disk write cache.
    /// Called AFTER DiskWriteCache.AddBlock succeeds. Returns the new written count.
    /// </summary>
    public int IncrementBlocksWritten() => Interlocked.Increment(ref _blocksWrittenCount);

    public void MarkBlockNotRequested(int begin)
    {
        int blockIndex = begin / _blockSize;
        if (blockIndex >= 0 && blockIndex < _blockCount)
            Interlocked.CompareExchange(ref _blockState[blockIndex], 0, 1);
    }

    public void MarkBlockRequested(int begin)
    {
        int blockIndex = begin / _blockSize;
        if (blockIndex >= 0 && blockIndex < _blockCount)
            Interlocked.CompareExchange(ref _blockState[blockIndex], 1, 0);
    }

    /// <summary>
    /// Resets requested flags for blocks requested by a specific peer.
    /// Called on peer disconnect to free orphaned blocks.
    /// </summary>
    public int ResetBlocksForPeer(string peerId)
    {
        int resetCount = 0;
        for (int i = 0; i < _blockCount; i++)
        {
            if (Volatile.Read(ref _blockState[i]) == 1 && _blockRequestedBy[i] == peerId)
            {
                if (Interlocked.CompareExchange(ref _blockState[i], 0, 1) == 1)
                {
                    _blockRequestedBy[i] = null;
                    resetCount++;
                }
            }
        }
        return resetCount;
    }

    /// <summary>
    /// Resets all requested flags for unreceived blocks.
    /// Preserves received blocks. Called on download stop/resume.
    /// </summary>
    public void ResetRequestedBlocks()
    {
        for (int i = 0; i < _blockCount; i++)
            Interlocked.CompareExchange(ref _blockState[i], 0, 1);
    }

    public bool IsBlockReceived(int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= _blockCount) return false;
        return Volatile.Read(ref _blockState[blockIndex]) == 2;
    }

    public bool IsBlockReceivedByOffset(int begin)
    {
        return IsBlockReceived(begin / _blockSize);
    }

    /// <summary>
    /// True if any block in this piece is still free (never requested). Cheap scan
    /// used by endgame gating: while a free block remains, normal picking can still
    /// make forward progress without resorting to duplicate requests.
    /// </summary>
    public bool HasUnrequestedBlocks()
    {
        for (int i = 0; i < _blockCount; i++)
        {
            if (Volatile.Read(ref _blockState[i]) == 0)
                return true;
        }
        return false;
    }

    public BlockRequest[] GetAllUnrequestedBlocks()
    {
        var blocks = new System.Collections.Generic.List<BlockRequest>();
        for (int i = 0; i < _blockCount; i++)
        {
            if (Volatile.Read(ref _blockState[i]) == 0)
            {
                int begin = i * _blockSize;
                int length = (int)Math.Min(_blockSize, _pieceSize - begin);
                blocks.Add(new BlockRequest(_pieceIndex, begin, length));
            }
        }
        return blocks.ToArray();
    }

    public BlockRequest[] GetRequestedNotReceivedBlocks()
    {
        var blocks = new System.Collections.Generic.List<BlockRequest>();
        for (int i = 0; i < _blockCount; i++)
        {
            if (Volatile.Read(ref _blockState[i]) == 1)
            {
                int begin = i * _blockSize;
                int length = (int)Math.Min(_blockSize, _pieceSize - begin);
                blocks.Add(new BlockRequest(_pieceIndex, begin, length));
            }
        }
        return blocks.ToArray();
    }

    /// <summary>
    /// Get all blocks not yet received (free or requested). For endgame mode.
    /// </summary>
    public BlockRequest[] GetAllPendingBlocks()
    {
        var blocks = new System.Collections.Generic.List<BlockRequest>();
        for (int i = 0; i < _blockCount; i++)
        {
            if (Volatile.Read(ref _blockState[i]) != 2)
            {
                int begin = i * _blockSize;
                int length = (int)Math.Min(_blockSize, _pieceSize - begin);
                blocks.Add(new BlockRequest(_pieceIndex, begin, length));
            }
        }
        return blocks.ToArray();
    }
}
