using System.Collections.Generic;
using System.Net;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Interfaces;

/// <summary>
/// Handles peer selection decisions for connection management.
/// Uses global peer priority to make consistent, attack-resistant decisions.
///
/// Instead of first-come-first-serve connection acceptance, this enables
/// priority-based eviction: when at max connections, disconnect the lowest
/// priority peer to make room for higher priority ones.
/// </summary>
public interface IPeerSelector
{
    /// <summary>
    /// Select a peer for disconnection when at connection limit.
    /// Returns the peer with lowest priority.
    /// </summary>
    /// <param name="connections">Current connected peers</param>
    /// <param name="localEndpoint">Our local endpoint for priority calculation</param>
    /// <returns>Peer to disconnect, or null if none eligible</returns>
    IPeerConnection SelectForDisconnection(
        IEnumerable<IPeerConnection> connections,
        IPEndPoint localEndpoint);

    /// <summary>
    /// Determines if a new connection should be accepted, potentially
    /// disconnecting a lower-priority existing connection.
    /// </summary>
    /// <param name="newPeer">The new peer wanting to connect</param>
    /// <param name="existingPeers">Currently connected peers</param>
    /// <param name="maxConnections">Maximum allowed connections</param>
    /// <param name="localEndpoint">Our local endpoint</param>
    /// <returns>Decision result containing whether to accept and who to disconnect</returns>
    ConnectionDecision ShouldAcceptConnection(
        PeerInfo newPeer,
        IEnumerable<IPeerConnection> existingPeers,
        int maxConnections,
        IPEndPoint localEndpoint);

    /// <summary>
    /// Select peers to keep when reducing connection count.
    /// Keeps highest priority peers.
    /// </summary>
    /// <param name="connections">Current connections</param>
    /// <param name="targetCount">How many to keep</param>
    /// <param name="localEndpoint">Our local endpoint</param>
    /// <returns>Peers to keep (highest priority)</returns>
    IReadOnlyList<IPeerConnection> SelectToKeep(
        IEnumerable<IPeerConnection> connections,
        int targetCount,
        IPEndPoint localEndpoint);
}

/// <summary>
/// Result of a connection acceptance decision.
/// </summary>
public readonly struct ConnectionDecision
{
    /// <summary>
    /// Whether the new connection should be accepted.
    /// </summary>
    public bool ShouldAccept { get; init; }

    /// <summary>
    /// If accepting requires disconnecting an existing peer, this is who.
    /// Null if under connection limit or if rejecting.
    /// </summary>
    public IPeerConnection PeerToDisconnect { get; init; }

    /// <summary>
    /// Priority of the new connection (for logging/debugging).
    /// </summary>
    public uint NewPeerPriority { get; init; }

    /// <summary>
    /// Priority of the peer being disconnected (for logging/debugging).
    /// </summary>
    public uint DisconnectedPeerPriority { get; init; }

    /// <summary>
    /// Reason for the decision.
    /// </summary>
    public string Reason { get; init; }

    public static ConnectionDecision Accept(string reason = "Under connection limit")
        => new() { ShouldAccept = true, Reason = reason };

    public static ConnectionDecision AcceptAndDisconnect(
        IPeerConnection toDisconnect,
        uint newPriority,
        uint oldPriority)
        => new()
        {
            ShouldAccept = true,
            PeerToDisconnect = toDisconnect,
            NewPeerPriority = newPriority,
            DisconnectedPeerPriority = oldPriority,
            Reason = $"New peer priority ({newPriority}) > existing ({oldPriority})"
        };

    public static ConnectionDecision Reject(string reason)
        => new() { ShouldAccept = false, Reason = reason };
}
