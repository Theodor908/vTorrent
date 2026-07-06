using System;
using System.Collections.Generic;
using System.Linq;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Core.Interfaces;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Bench.Simulation;

/// <summary>
/// Minimal IPeerRegistry backed by FakePeerManager's synthetic peer pool.
/// Satisfies DownloadCoordinator's dependency without real state tracking.
/// </summary>
public sealed class FakePeerRegistry : IPeerRegistry
{
    private readonly FakePeerManager _peerManager;

    public FakePeerRegistry(FakePeerManager peerManager)
    {
        _peerManager = peerManager;
    }

    // ------------------------------------------------------------------
    // IPeerRegistry properties
    // ------------------------------------------------------------------

    public int TotalPeerCount => _peerManager.ConnectedPeerCount;
    public int ConnectedPeerCount => _peerManager.ConnectedPeerCount;

    // ------------------------------------------------------------------
    // Query methods
    // ------------------------------------------------------------------

    public IReadOnlyList<IPeerConnection> GetAllConnectedPeers()
        => _peerManager.ConnectedPeers;

    public bool TryGetConnected(string key, out IPeerConnection connection)
    {
        foreach (var peer in _peerManager.ConnectedPeers)
        {
            if (peer.EndpointString == key)
            {
                connection = peer;
                return true;
            }
        }
        connection = null!;
        return false;
    }

    public bool TryGetPeer(string key, out PeerState state)
    {
        state = null!;
        return false;
    }

    public bool TryGetPeer(PeerInfo info, out PeerState state)
    {
        state = null!;
        return false;
    }

    public IReadOnlyList<PeerState> GetAllByStatus(PeerConnectionStatus status)
        => Array.Empty<PeerState>();

    public IReadOnlyList<PeerState> GetTopByScore(int count)
        => Array.Empty<PeerState>();

    public IReadOnlyList<PeerState> GetPeersWhere(Func<PeerState, bool> predicate)
        => Array.Empty<PeerState>();

    // ------------------------------------------------------------------
    // No-op mutation methods
    // ------------------------------------------------------------------

    public PeerState? GetOrRegister(PeerInfo info) => null;

    public void UpdateConnection(string key, IPeerConnection connection, PeerConnectionStatus status) { }

    // No status tracking (UpdateConnection is a no-op), so every claim succeeds.
    public bool TryBeginConnecting(string key) => true;

    public void Ban(string key, TimeSpan duration, string reason) { }

    public bool IsBanned(string key) => false;

    public bool Remove(string key) => false;

    public void Clear() { }

    public string GetPeerKey(PeerInfo info) => $"{info.IpAddress}:{info.Port}";

    public int GetFailCount(string key) => 0;

    public void RecordConnectionFailure(string key) { }

    public void RecordConnectionSuccess(string key) { }
}
