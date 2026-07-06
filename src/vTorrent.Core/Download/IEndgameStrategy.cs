using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Download;

/// <summary>
/// Strategy for handling duplicate block requests when no unique blocks are available.
/// Aligned with libtorrent: endgame is per-peer, per-request-cycle — not a global flag.
/// The coordinator calls PickDuplicateBlock only after normal picking returns nothing.
/// </summary>
public interface IEndgameStrategy
{
    /// <summary>
    /// Total bytes wasted from duplicate blocks received during endgame.
    /// </summary>
    long WastedBytes { get; }

    /// <summary>
    /// Count of duplicate blocks received.
    /// </summary>
    int DuplicateBlockCount { get; }

    /// <summary>
    /// Pick ONE block already requested by a different peer.
    /// Called only when normal piece picking returned no unique blocks for this peer.
    /// Returns null if no suitable duplicate is found.
    /// </summary>
    BlockRequest? PickDuplicateBlock(
        IPeerConnection peer,
        IReadOnlyDictionary<int, PieceBlockTracker> inProgressPieces,
        ConcurrentDictionary<BlockRequest, PendingBlock> pendingBlocks,
        Func<IPeerConnection, int, bool> peerHasPiece);

    /// <summary>
    /// Pick up to maxBlocks duplicate blocks requested by different peers.
    /// libtorrent fills the entire pipeline with duplicates in endgame — not just one.
    /// Returns shuffled candidates to spread load across pieces.
    /// </summary>
    List<BlockRequest> PickDuplicateBlocks(
        IPeerConnection peer,
        IReadOnlyDictionary<int, PieceBlockTracker> inProgressPieces,
        ConcurrentDictionary<BlockRequest, PendingBlock> pendingBlocks,
        Func<IPeerConnection, int, bool> peerHasPiece,
        int maxBlocks);

    /// <summary>
    /// Called when a block is received. Tracks duplicates for waste accounting.
    /// Returns true if duplicate (waste), false if first copy.
    /// </summary>
    bool OnBlockReceived(BlockRequest block, IPeerConnection fromPeer);

    /// <summary>
    /// Clean up tracking for a disconnected peer.
    /// </summary>
    void OnPeerDisconnected(IPeerConnection peer);

    /// <summary>
    /// Reset all tracking state.
    /// </summary>
    void Reset();
}
