using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Download;

/// <summary>
/// No-op endgame strategy. Never picks duplicate blocks.
/// </summary>
public class NullEndgameStrategy : IEndgameStrategy
{
    public long WastedBytes => 0;
    public int DuplicateBlockCount => 0;

    public BlockRequest? PickDuplicateBlock(
        IPeerConnection peer,
        IReadOnlyDictionary<int, PieceBlockTracker> inProgressPieces,
        ConcurrentDictionary<BlockRequest, PendingBlock> pendingBlocks,
        Func<IPeerConnection, int, bool> peerHasPiece)
        => null;

    public List<BlockRequest> PickDuplicateBlocks(
        IPeerConnection peer,
        IReadOnlyDictionary<int, PieceBlockTracker> inProgressPieces,
        ConcurrentDictionary<BlockRequest, PendingBlock> pendingBlocks,
        Func<IPeerConnection, int, bool> peerHasPiece,
        int maxBlocks)
        => new(0);

    public bool OnBlockReceived(BlockRequest block, IPeerConnection fromPeer)
        => false;

    public void OnPeerDisconnected(IPeerConnection peer) { }
    public void Reset() { }
}
