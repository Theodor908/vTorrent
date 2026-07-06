using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using vTorrent.Core.Interfaces;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Upload;

/// <summary>
/// Detects peers that are "snubbing" - unchoked but not sending data after we requested.
///
/// A peer is snubbing if:
/// 1. They are not choking us (we can request from them)
/// 2. We are interested in them (they have pieces we want)
/// 3. We have sent them requests
/// 4. They haven't sent us any data for SnubTimeout (60 seconds) since our first request
///
/// IMPORTANT: We only mark peers as snubbing if we've actually requested from them.
/// Peers we haven't requested from yet are NOT snubbing - they just haven't had a chance.
/// </summary>
public class SnubbingDetector : ISnubbingDetector
{
    private readonly ConcurrentDictionary<IPeerConnection, DateTime> _lastBlockReceived = new();
    private readonly ConcurrentDictionary<IPeerConnection, DateTime> _firstRequestSent = new();
    private readonly ILogger<SnubbingDetector> _logger;

    /// <summary>
    /// Time without receiving data before considering a peer as snubbing.
    /// BitTorrent convention is 60 seconds.
    /// </summary>
    public TimeSpan SnubTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public SnubbingDetector(ILogger<SnubbingDetector> logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Records that we sent a request to a peer.
    /// This starts their snubbing timer if not already started.
    /// </summary>
    public void RecordRequestSent(IPeerConnection peer)
    {
        if (peer == null) return;
        _firstRequestSent.TryAdd(peer, DateTime.UtcNow);
    }

    /// <summary>
    /// Records that we received a block from a peer.
    /// This resets their snubbing timer.
    /// </summary>
    public void RecordBlockReceived(IPeerConnection peer)
    {
        if (peer == null) return;
        _lastBlockReceived[peer] = DateTime.UtcNow;
    }

    /// <summary>
    /// Checks if a peer is currently snubbing us.
    /// </summary>
    public bool IsSnubbing(IPeerConnection peer)
    {
        if (peer == null) return false;

        // Peer must be:
        // 1. Not choking us (we can request from them)
        // 2. We are interested (they have pieces we want)
        if (peer.IsChoked || !peer.IsInterested)
            return false;

        // CRITICAL: Only consider snubbing if we've actually sent requests to this peer
        // If we haven't requested anything, they can't be snubbing - they just haven't had a chance
        if (!_firstRequestSent.TryGetValue(peer, out var firstRequest))
        {
            // Never sent a request to this peer - NOT snubbing
            return false;
        }

        // Check if we've received data recently
        if (_lastBlockReceived.TryGetValue(peer, out var lastReceived))
        {
            var timeSinceLastBlock = DateTime.UtcNow - lastReceived;
            bool isSnubbing = timeSinceLastBlock > SnubTimeout;

            if (isSnubbing)
            {
                _logger?.LogDebug("Peer {Peer} snubbing - no data for {Duration:F0}s since last block",
                    peer.PeerInfo?.EndPoint, timeSinceLastBlock.TotalSeconds);
            }

            return isSnubbing;
        }

        // We've sent requests but never received anything
        var timeSinceFirstRequest = DateTime.UtcNow - firstRequest;
        if (timeSinceFirstRequest > SnubTimeout)
        {
            _logger?.LogDebug("Peer {Peer} snubbing - no data for {Duration:F0}s since first request",
                peer.PeerInfo?.EndPoint, timeSinceFirstRequest.TotalSeconds);
            return true;
        }

        // Still within grace period since first request
        return false;
    }

    /// <summary>
    /// Gets the time since we last received data from a peer.
    /// </summary>
    public TimeSpan? GetTimeSinceLastBlock(IPeerConnection peer)
    {
        if (peer == null) return null;

        if (_lastBlockReceived.TryGetValue(peer, out var lastReceived))
        {
            return DateTime.UtcNow - lastReceived;
        }

        return null;
    }

    /// <summary>
    /// Filters peers to find those that are snubbing.
    /// </summary>
    public IEnumerable<IPeerConnection> GetSnubbingPeers(IEnumerable<IPeerConnection> peers)
    {
        if (peers == null) yield break;

        foreach (var peer in peers)
        {
            if (IsSnubbing(peer))
            {
                yield return peer;
            }
        }
    }

    /// <summary>
    /// Clean up tracking data for disconnected peer.
    /// </summary>
    public void OnPeerDisconnected(IPeerConnection peer)
    {
        if (peer == null) return;
        _lastBlockReceived.TryRemove(peer, out _);
        _firstRequestSent.TryRemove(peer, out _);
    }
}
