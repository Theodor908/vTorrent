using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Download;

/// <summary>
/// Per-peer duplicate block selection aligned with libtorrent.
/// No global flag, no threshold. Called only when normal picking
/// returns no unique blocks for a peer.
/// </summary>
public class EndgameManager : IEndgameStrategy
{
    private readonly ILogger<EndgameManager> _logger;

    // Tracks which blocks have been received (for duplicate detection)
    private readonly ConcurrentDictionary<BlockRequest, bool> _receivedBlocks = new();

    // Waste tracking
    private long _wastedBytes;
    private int _duplicateBlockCount;

    // RNG for random block selection (libtorrent picks randomly)
    [ThreadStatic] private static Random? t_random;
    private static Random Random => t_random ??= new Random();

    public long WastedBytes => Interlocked.Read(ref _wastedBytes);
    public int DuplicateBlockCount => _duplicateBlockCount;

    private bool _strictEndgameMode = true;

    public void SetStrictEndgameMode(bool strict) => _strictEndgameMode = strict;

    public EndgameManager(ILogger<EndgameManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Scans in-progress pieces for blocks requested by a different peer.
    /// Returns ONE random candidate, or null if none found.
    /// </summary>
    public BlockRequest? PickDuplicateBlock(
        IPeerConnection peer,
        IReadOnlyDictionary<int, PieceBlockTracker> inProgressPieces,
        ConcurrentDictionary<BlockRequest, PendingBlock> pendingBlocks,
        Func<IPeerConnection, int, bool> peerHasPiece)
    {
        var blocks = PickDuplicateBlocks(peer, inProgressPieces, pendingBlocks, peerHasPiece, 1);
        return blocks.Count > 0 ? blocks[0] : null;
    }

    /// <summary>
    /// Scans in-progress pieces for ALL unreceived blocks the peer can provide.
    /// Returns up to maxBlocks shuffled candidates to fill the pipeline.
    ///
    /// Uses GetAllPendingBlocks() (all unreceived blocks) instead of checking
    /// _pendingBlocks. This matches libtorrent's endgame: request every unreceived
    /// block from every peer that has the piece. Duplicates are handled on receive.
    ///
    /// Why not check _pendingBlocks: SelectBlockBatch runs under _pieceLock but
    /// _pendingBlocks is updated AFTER the lock is released. Between those points,
    /// blocks are invisible to other peers' endgame picking, causing stalls when
    /// few pieces remain and all peers compete for the same blocks.
    /// </summary>
    public List<BlockRequest> PickDuplicateBlocks(
        IPeerConnection peer,
        IReadOnlyDictionary<int, PieceBlockTracker> inProgressPieces,
        ConcurrentDictionary<BlockRequest, PendingBlock> pendingBlocks,
        Func<IPeerConnection, int, bool> peerHasPiece,
        int maxBlocks)
    {
        List<BlockRequest>? candidates = null;

        foreach (var (pieceIndex, progress) in inProgressPieces)
        {
            if (!peerHasPiece(peer, pieceIndex))
                continue;

            // All unreceived blocks — regardless of requested state or pending tracking.
            // This matches libtorrent's endgame flood and the legacy
            // RequestAllPendingBlocksInEndgameAsync behavior.
            var unreceived = progress.GetAllPendingBlocks();
            if (unreceived.Length > 0)
            {
                candidates ??= new List<BlockRequest>();
                candidates.AddRange(unreceived);
            }
        }

        if (candidates == null || candidates.Count == 0)
            return new List<BlockRequest>(0);

        // Shuffle candidates (Fisher-Yates) to spread load across pieces
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = Random.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        // libtorrent strict_end_game_mode: 1 duplicate per peer to limit waste.
        // Pipelining is disabled during endgame — each peer gets exactly one
        // outstanding duplicate request at a time.
        int count = _strictEndgameMode ? 1 : Math.Min(maxBlocks, candidates.Count);
        if (count < candidates.Count)
            candidates.RemoveRange(count, candidates.Count - count);

        _logger.LogDebug("Endgame: filling pipeline with {Count} blocks from {Pieces} in-progress pieces",
            candidates.Count, inProgressPieces.Count);

        return candidates;
    }

    public bool OnBlockReceived(BlockRequest block, IPeerConnection fromPeer)
    {
        if (!_receivedBlocks.TryAdd(block, true))
        {
            // Duplicate
            Interlocked.Add(ref _wastedBytes, block.Length);
            Interlocked.Increment(ref _duplicateBlockCount);
            _logger.LogDebug("Endgame waste: duplicate block {Piece}:{Offset} ({Length} bytes)",
                block.PieceIndex, block.Begin, block.Length);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Clears received-block tracking for a specific piece.
    /// Called when a piece fails hash verification and must be re-downloaded.
    /// Without this, re-requested blocks would be falsely rejected as duplicates.
    /// </summary>
    public void ClearPieceBlocks(int pieceIndex, int blockSize, long pieceSize)
    {
        int blockCount = (int)Math.Ceiling((double)pieceSize / blockSize);
        for (int i = 0; i < blockCount; i++)
        {
            int begin = i * blockSize;
            int length = (int)Math.Min(blockSize, pieceSize - begin);
            _receivedBlocks.TryRemove(new BlockRequest(pieceIndex, begin, length), out _);
        }
    }

    public void OnPeerDisconnected(IPeerConnection peer)
    {
        // No per-peer tracking needed — pendingBlocks in coordinator handles cleanup
    }

    public void Reset()
    {
        _receivedBlocks.Clear();
        Interlocked.Exchange(ref _wastedBytes, 0);
        Interlocked.Exchange(ref _duplicateBlockCount, 0);
        _logger.LogDebug("Endgame state reset");
    }
}
