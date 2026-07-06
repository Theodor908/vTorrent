using System;

using System.Collections.Generic;

using vTorrent.Abstractions.Enums;

using vTorrent.Core.PeerCommunication.Models;

using vTorrent.Abstractions.Models;
using vTorrent.Core.PeerCommunication;

namespace vTorrent.Core.Interfaces;

/// <summary>

/// Abstracts peer state management - single source of truth for all peer information.

/// Enables testing and alternative implementations.

/// </summary>

public interface IPeerRegistry

{

    /// <summary>

    /// Total number of peers in the registry (all states).

    /// </summary>

    int TotalPeerCount { get; }

    /// <summary>

    /// Number of currently connected peers.

    /// </summary>

    int ConnectedPeerCount { get; }

    /// <summary>

    /// Gets or creates a peer state entry.
    /// Returns null if the peer list is full (MaxPeerlistSize reached) and the peer is not already registered.

    /// </summary>

    PeerState? GetOrRegister(PeerInfo info);

    /// <summary>

    /// Tries to get a connected peer by key.

    /// </summary>

    bool TryGetConnected(string key, out IPeerConnection connection);

    /// <summary>

    /// Gets a peer state by key.

    /// </summary>

    bool TryGetPeer(string key, out PeerState state);

    /// <summary>

    /// Gets a peer state by PeerInfo.

    /// </summary>

    bool TryGetPeer(PeerInfo info, out PeerState state);

    /// <summary>

    /// Gets all peers matching a specific status.

    /// </summary>

    IReadOnlyList<PeerState> GetAllByStatus(PeerConnectionStatus status);

    /// <summary>

    /// Gets all connected peers.

    /// </summary>

    IReadOnlyList<IPeerConnection> GetAllConnectedPeers();

    /// <summary>

    /// Updates peer connection and status atomically.

    /// </summary>

    void UpdateConnection(string key, IPeerConnection connection, PeerConnectionStatus status);

    /// <summary>Atomic dial claim: Connecting transition, false if already Connecting/Connected.</summary>

    bool TryBeginConnecting(string key);

    /// <summary>

    /// Bans a peer for the specified duration.

    /// </summary>

    void Ban(string key, TimeSpan duration, string reason);

    /// <summary>

    /// Checks if a peer is currently banned.

    /// </summary>

    bool IsBanned(string key);

    /// <summary>

    /// Gets top N peers by priority score.

    /// </summary>

    IReadOnlyList<PeerState> GetTopByScore(int count);

    /// <summary>

    /// Gets all peers matching a filter predicate.

    /// </summary>

    IReadOnlyList<PeerState> GetPeersWhere(Func<PeerState, bool> predicate);

    /// <summary>

    /// Removes a peer from the registry.

    /// </summary>

    bool Remove(string key);

    /// <summary>

    /// Clears all peers from the registry.

    /// </summary>

    void Clear();

    /// <summary>

    /// Gets the standardized key for a peer.

    /// </summary>

    string GetPeerKey(PeerInfo info);

    /// <summary>

    /// Gets the fail count for a peer (number of failed connection attempts).

    /// Used for priority-based peer selection (libtorrent-style).

    /// </summary>

    int GetFailCount(string key);

    /// <summary>

    /// Records a connection failure for a peer.

    /// Increments the fail count used for priority calculations.

    /// </summary>

    void RecordConnectionFailure(string key);

    /// <summary>

    /// Records a successful connection for a peer.

    /// Updates success stats for priority calculations.

    /// </summary>

    void RecordConnectionSuccess(string key);

}

/// <summary>

/// Represents the complete state of a peer in the session.

/// </summary>

public class PeerState

{

    public PeerInfo Info { get; init; }

    public IPeerConnection Connection { get; set; }

    public PeerScore Score { get; init; }

    public PeerConnectionStatus Status { get; set; }

    public DateTime? BannedUntil { get; set; }

    public DateTime RegisteredAt { get; init; }

    public DateTime? LastConnectedAt { get; set; }

}
