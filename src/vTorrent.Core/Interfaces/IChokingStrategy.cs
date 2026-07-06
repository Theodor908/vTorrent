using System;

using System.Collections.Generic;

using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.Engine;

namespace vTorrent.Core.Interfaces;

/// <summary>

/// Strategy interface for the BitTorrent choking algorithm.

/// Allows different choking implementations to be plugged in (Strategy Pattern).

///

/// The choking algorithm determines which peers receive upload slots.

/// Standard BitTorrent uses Tit-for-Tat: reward peers who send us data.

/// </summary>

public interface IChokingStrategy

{

    /// <summary>

    /// How often to re-evaluate choking decisions.

    /// BitTorrent spec recommends 15 seconds.

    /// </summary>

    TimeSpan RechokingInterval { get; }

    /// <summary>

    /// How often to rotate the optimistic unchoke slot.

    /// Typically 30 seconds (2 rechoking intervals).

    /// </summary>

    TimeSpan OptimisticRotationInterval { get; }

    /// <summary>

    /// Total number of upload slots (regular + optimistic).

    /// </summary>

    int TotalUploadSlots { get; }

    /// <summary>

    /// Number of slots reserved for optimistic unchoking.

    /// Typically 1.

    /// </summary>

    int OptimisticSlots { get; }

    /// <summary>

    /// Number of regular (rate-based) unchoke slots.

    /// </summary>

    int RegularSlots => TotalUploadSlots - OptimisticSlots;

    /// <summary>

    /// Select peers for regular (rate-based) unchoking.

    /// </summary>

    /// <param name="candidates">All interested, connected peers</param>

    /// <param name="statistics">Statistics tracker for rate information</param>

    /// <param name="isSeeding">True if we're seeding (affects rate metric used)</param>

    /// <returns>Peers to unchoke (up to RegularSlots count)</returns>

    IReadOnlyList<IPeerConnection> SelectRegularUnchokes(

        IEnumerable<IPeerConnection> candidates,

        IStatisticsTracker statistics,

        bool isSeeding);

    /// <summary>

    /// Select a peer for optimistic unchoking.

    /// Optimistic unchokes allow new peers to prove themselves.

    /// </summary>

    /// <param name="candidates">Peers eligible for optimistic unchoke (interested but not already unchoked)</param>

    /// <param name="currentOptimistic">Current optimistic peer (may be retained or rotated)</param>

    /// <returns>Peer to use for optimistic slot, or null</returns>

    IPeerConnection SelectOptimisticUnchoke(

        IEnumerable<IPeerConnection> candidates,

        IPeerConnection currentOptimistic);

    /// <summary>

    /// Determine if a peer should receive immediate unchoke (fast peer optimization).

    /// Fast peers may bypass the normal choking cycle.

    /// </summary>

    /// <param name="peer">The peer to evaluate</param>

    /// <param name="downloadRate">Peer's current download rate to us</param>

    /// <param name="currentUnchokedCount">Current number of unchoked peers</param>

    /// <returns>True if peer should be immediately unchoked</returns>

    bool ShouldImmediatelyUnchoke(IPeerConnection peer, double downloadRate, int currentUnchokedCount);

}
