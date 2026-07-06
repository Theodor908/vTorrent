using System;
using System.Collections.Generic;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Interfaces;

/// <summary>
/// Detects "snubbing" peers - peers that have been unchoked but stop sending data.
///
/// A peer is considered snubbing if:
/// 1. We have unchoked them (giving them upload slots)
/// 2. They have pieces we want (we are interested)
/// 3. They have not sent us any data for SnubTimeout duration
///
/// Snubbing detection prevents wasting upload slots on unresponsive peers.
/// </summary>
public interface ISnubbingDetector
{
    /// <summary>
    /// Time without receiving data before a peer is considered snubbing.
    /// Typically 60 seconds.
    /// </summary>
    TimeSpan SnubTimeout { get; }

    /// <summary>
    /// Checks if a specific peer is currently snubbing us.
    /// </summary>
    /// <param name="peer">The peer to check</param>
    /// <returns>True if peer is snubbing</returns>
    bool IsSnubbing(IPeerConnection peer);

    /// <summary>
    /// Records that we sent a request to a peer.
    /// Starts their snubbing timer if not already started.
    /// </summary>
    /// <param name="peer">The peer we sent a request to</param>
    void RecordRequestSent(IPeerConnection peer);

    /// <summary>
    /// Records that we received a block from a peer.
    /// Resets their snubbing timer.
    /// </summary>
    /// <param name="peer">The peer that sent data</param>
    void RecordBlockReceived(IPeerConnection peer);

    /// <summary>
    /// Gets the time since we last received data from a peer.
    /// </summary>
    /// <param name="peer">The peer to check</param>
    /// <returns>Time since last block, or null if never received</returns>
    TimeSpan? GetTimeSinceLastBlock(IPeerConnection peer);

    /// <summary>
    /// Filters a collection of peers to find those that are snubbing.
    /// </summary>
    /// <param name="peers">Peers to check</param>
    /// <returns>Peers that are snubbing</returns>
    IEnumerable<IPeerConnection> GetSnubbingPeers(IEnumerable<IPeerConnection> peers);

    /// <summary>
    /// Called when a peer disconnects to clean up tracking state.
    /// </summary>
    /// <param name="peer">The disconnected peer</param>
    void OnPeerDisconnected(IPeerConnection peer);
}
