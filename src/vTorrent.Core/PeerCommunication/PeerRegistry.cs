using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Interfaces;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.PeerCommunication;

/// <summary>
/// Centralized registry for tracking all peer state across the torrent session.
/// Single source of truth for peer connections, scores, and status.
/// Implements IPeerRegistry for dependency injection and testability.
/// </summary>
public class PeerRegistry : IPeerRegistry
{
    private readonly ConcurrentDictionary<string, PeerState> _peers = new();
    private readonly ConcurrentDictionary<string, IPeerConnection> _connectedPeers = new();
    private readonly object _lock = new();
    private readonly IOptionsMonitor<PeerSettings>? _peerMonitor;

    public PeerRegistry(IOptionsMonitor<PeerSettings>? peerMonitor = null)
    {
        _peerMonitor = peerMonitor;
    }

    public int TotalPeerCount => _peers.Count;
    public int ConnectedPeerCount => _connectedPeers.Count;

    /// <summary>
    /// Gets or creates a peer state entry.
    /// Returns null if the peer list is full (MaxPeerlistSize reached) and the peer is not already registered.
    /// </summary>
    public PeerState? GetOrRegister(PeerInfo info)
    {
        if (info == null)
            throw new ArgumentNullException(nameof(info));

        var key = GetPeerKey(info);

        var maxSize = _peerMonitor?.CurrentValue.MaxPeerlistSize ?? 3000;
        if (!_peers.ContainsKey(key) && _peers.Count >= maxSize)
        {
            // Peer list is full — reject this peer
            return null;
        }

        return _peers.GetOrAdd(key, _ => new PeerState
        {
            Info = info,
            Score = new PeerScore(info),
            Status = PeerConnectionStatus.Discovered,
            RegisteredAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Tries to get a connected peer by key.
    /// </summary>
    public bool TryGetConnected(string key, out IPeerConnection connection)
    {
        if (_peers.TryGetValue(key, out var state) &&
            state.Status == PeerConnectionStatus.Connected &&
            state.Connection != null)
        {
            connection = state.Connection;
            return true;
        }
        connection = null;
        return false;
    }

    /// <summary>
    /// Gets a peer state by key.
    /// </summary>
    public bool TryGetPeer(string key, out PeerState state)
    {
        return _peers.TryGetValue(key, out state);
    }

    /// <summary>
    /// Gets a peer state by PeerInfo.
    /// </summary>
    public bool TryGetPeer(PeerInfo info, out PeerState state)
    {
        return TryGetPeer(GetPeerKey(info), out state);
    }

    /// <summary>
    /// Gets all peers matching a specific status.
    /// </summary>
    public IReadOnlyList<PeerState> GetAllByStatus(PeerConnectionStatus status)
    {
        return _peers.Values
            .Where(p => p.Status == status)
            .ToList();
    }

    /// <summary>
    /// Gets all connected peers.
    /// </summary>
    public IReadOnlyList<IPeerConnection> GetAllConnectedPeers()
    {
        return _connectedPeers.Values.ToList();
    }

    /// <summary>
    /// Updates peer connection and status atomically.
    /// </summary>
    public void UpdateConnection(string key, IPeerConnection connection, PeerConnectionStatus status)
    {
        if (_peers.TryGetValue(key, out var state))
        {
            lock (_lock)
            {
                state.Connection = connection;
                state.Status = status;

                if (status == PeerConnectionStatus.Connected)
                {
                    state.LastConnectedAt = DateTime.UtcNow;
                    state.Score.CurrentlyConnected = true;
                    if (connection != null)
                        _connectedPeers[key] = connection;
                }
                else
                {
                    _connectedPeers.TryRemove(key, out _);
                    if (status == PeerConnectionStatus.Disconnected)
                    {
                        state.Score.CurrentlyConnected = false;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Atomically claims a peer for an outgoing dial: transitions it to Connecting
    /// and returns true only if it is not already Connecting or Connected. Closes
    /// the double-dial window between concurrent peer-add paths (DHT/PEX events vs.
    /// the connect-boost drain loop). Callers MUST reset the status (Connected or
    /// Disconnected) when the dial completes — AddPeerAsync's success/failure paths
    /// already do.
    /// </summary>
    public bool TryBeginConnecting(string key)
    {
        if (!_peers.TryGetValue(key, out var state))
            return false;

        lock (_lock)
        {
            if (state.Status is PeerConnectionStatus.Connecting or PeerConnectionStatus.Connected)
                return false;

            state.Status = PeerConnectionStatus.Connecting;
            return true;
        }
    }

    /// <summary>
    /// Bans a peer for the specified duration.
    /// </summary>
    public void Ban(string key, TimeSpan duration, string reason)
    {
        if (_peers.TryGetValue(key, out var state))
        {
            lock (_lock)
            {
                state.BannedUntil = DateTime.UtcNow + duration;
                state.Status = PeerConnectionStatus.Banned;
                state.Score.BanCount++;
                state.Score.LastBanReason = reason;
            }
        }
    }

    /// <summary>
    /// Checks if a peer is currently banned.
    /// </summary>
    public bool IsBanned(string key)
    {
        if (_peers.TryGetValue(key, out var state))
        {
            if (state.Status == PeerConnectionStatus.Banned)
            {
                // Check if ban has expired
                if (state.BannedUntil.HasValue && state.BannedUntil.Value > DateTime.UtcNow)
                {
                    return true;
                }
                else
                {
                    // Ban expired, update status
                    lock (_lock)
                    {
                        state.Status = PeerConnectionStatus.Disconnected;
                        state.BannedUntil = null;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Gets top N peers by priority score.
    /// </summary>
    public IReadOnlyList<PeerState> GetTopByScore(int count)
    {
        return _peers.Values
            .Where(p => p.Status != PeerConnectionStatus.Banned)
            .OrderByDescending(p => p.Score.Priority)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Gets all peers matching a filter predicate.
    /// </summary>
    public IReadOnlyList<PeerState> GetPeersWhere(Func<PeerState, bool> predicate)
    {
        return _peers.Values.Where(predicate).ToList();
    }

    /// <summary>
    /// Removes a peer from the registry.
    /// </summary>
    public bool Remove(string key)
    {
        _connectedPeers.TryRemove(key, out _);
        return _peers.TryRemove(key, out _);
    }

    /// <summary>
    /// Clears all peers from the registry.
    /// </summary>
    public void Clear()
    {
        _connectedPeers.Clear();
        _peers.Clear();
    }

    /// <summary>
    /// Gets the fail count for a peer (number of failed connection attempts).
    /// Used for priority-based peer selection (libtorrent-style).
    /// </summary>
    public int GetFailCount(string key)
    {
        if (_peers.TryGetValue(key, out var state))
        {
            return state.Score.FailedConnections;
        }
        return 0;
    }

    /// <summary>
    /// Records a connection failure for a peer.
    /// Increments the fail count used for priority calculations.
    /// </summary>
    public void RecordConnectionFailure(string key)
    {
        if (_peers.TryGetValue(key, out var state))
        {
            lock (_lock)
            {
                state.Score.FailedConnections++;
                state.Score.ConnectionAttempts++;
                state.Score.LastFailure = DateTime.UtcNow;
                state.Score.UpdatePriority();
            }
        }
    }

    /// <summary>
    /// Records a successful connection for a peer.
    /// Updates success stats for priority calculations.
    /// </summary>
    public void RecordConnectionSuccess(string key)
    {
        if (_peers.TryGetValue(key, out var state))
        {
            lock (_lock)
            {
                state.Score.SuccessfulConnections++;
                state.Score.ConnectionAttempts++;
                state.Score.LastConnected = DateTime.UtcNow;
                state.Score.UpdatePriority();
            }
        }
    }

    /// <summary>
    /// Gets the standardized key for a peer (instance method for interface).
    /// </summary>
    string IPeerRegistry.GetPeerKey(PeerInfo info)
    {
        return GetPeerKey(info);
    }

    /// <summary>
    /// Gets the standardized key for a peer (static for convenience).
    /// </summary>
    public static string GetPeerKey(PeerInfo info)
    {
        return $"{info.IpAddress}:{info.Port}";
    }

    // Note: PeerState and PeerConnectionStatus are defined in Core/Interfaces/IPeerRegistry.cs
}

/// <summary>
/// Tracks quality metrics and performance statistics for a peer.
/// </summary>
public class PeerScore
{
    public PeerInfo PeerInfo { get; }
    public string Source { get; set; } = "Unknown";
    public DateTime FirstSeen { get; }
    public DateTime LastSeen { get; set; }
    public DateTime? LastConnected { get; set; }
    public DateTime? LastFailure { get; set; }
    public bool CurrentlyConnected { get; set; }

    // Connection stats
    public int ConnectionAttempts { get; set; }
    public int SuccessfulConnections { get; set; }
    public int FailedConnections { get; set; }
    public int DisconnectionCount { get; set; }
    public TimeSpan TotalConnectionTime { get; set; }

    // Transfer stats (updated from TorrentStatisticsTracker)
    public double DownloadRate { get; set; }
    public double UploadRate { get; set; }
    public long TotalDownloaded { get; set; }
    public long TotalUploaded { get; set; }

    // Quality metrics
    public int ProtocolViolations { get; set; }
    public int StallCount { get; set; }
    public int BanCount { get; set; }
    public string LastBanReason { get; set; } = string.Empty;

    /// <summary>
    /// Trust points based on piece verification results.
    /// Range: -7 to +8 (like libtorrent).
    /// +1 for valid piece contribution, -2 for invalid piece.
    /// Peer is auto-banned when reaching -7.
    /// </summary>
    public sbyte TrustPoints { get; set; }

    /// <summary>
    /// Whether this peer is on parole (sent bad data, needs isolation).
    /// When on parole, only request whole pieces to identify bad data source.
    /// </summary>
    public bool OnParole { get; set; }

    /// <summary>
    /// Number of pieces that failed hash verification with this peer's contribution.
    /// </summary>
    public int HashFailures { get; set; }

    /// <summary>
    /// Number of pieces that passed hash verification with this peer's contribution.
    /// </summary>
    public int ValidPieces { get; set; }

    /// <summary>
    /// Calculated priority score (0-1, higher is better).
    /// Used for peer selection and replacement decisions.
    /// </summary>
    public double Priority { get; set; }

    public PeerScore(PeerInfo peerInfo)
    {
        PeerInfo = peerInfo ?? throw new ArgumentNullException(nameof(peerInfo));
        FirstSeen = DateTime.UtcNow;
        LastSeen = DateTime.UtcNow;
    }

    /// <summary>
    /// Updates the priority score based on current metrics.
    /// </summary>
    public void UpdatePriority()
    {
        double score = 0.5; // Base score

        // Success rate contribution (0-0.3)
        if (ConnectionAttempts > 0)
        {
            double successRate = (double)SuccessfulConnections / ConnectionAttempts;
            score += successRate * 0.3;
        }

        // Transfer rate contribution (0-0.3)
        double avgRate = (DownloadRate + UploadRate) / 2;
        double rateScore = Math.Min(avgRate / (1024 * 1024), 1.0); // Normalize to 1MB/s
        score += rateScore * 0.3;

        // Stability contribution (0-0.2)
        if (TotalConnectionTime.TotalSeconds > 0)
        {
            double avgConnectionTime = TotalConnectionTime.TotalSeconds / Math.Max(DisconnectionCount, 1);
            double stabilityScore = Math.Min(avgConnectionTime / 3600, 1.0); // Normalize to 1 hour
            score += stabilityScore * 0.2;
        }

        // Quality penalties
        score -= ProtocolViolations * 0.1;
        score -= StallCount * 0.05;
        score -= BanCount * 0.2;

        // Clamp to 0-1
        Priority = Math.Clamp(score, 0.0, 1.0);
    }

    /// <summary>
    /// Records a valid piece contribution from this peer.
    /// Increases trust by 1 (capped at +8).
    /// Removes parole status if active.
    /// </summary>
    public void OnValidPiece()
    {
        ValidPieces++;
        OnParole = false;  // Successful piece removes parole
        TrustPoints = Math.Min((sbyte)8, (sbyte)(TrustPoints + 1));
        UpdatePriority();
    }

    /// <summary>
    /// Records an invalid piece (hash failure) with this peer's contribution.
    /// Decreases trust by 2 (capped at -7).
    /// Returns true if peer should be banned (trust <= -7).
    /// </summary>
    public bool OnInvalidPiece()
    {
        HashFailures++;
        OnParole = true;  // Put on parole after hash failure
        TrustPoints = Math.Max((sbyte)-7, (sbyte)(TrustPoints - 2));
        UpdatePriority();
        return TrustPoints <= -7;
    }

    /// <summary>
    /// Checks if peer should be banned based on trust points.
    /// </summary>
    public bool ShouldBeBanned => TrustPoints <= -7;
}
