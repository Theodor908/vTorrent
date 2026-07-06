using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using vTorrent.Core.Interfaces;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Enums;

namespace vTorrent.Core.Upload;

/// <summary>
/// Peer discovery source ranking (libtorrent-style).
/// Higher values indicate more trusted/reliable sources.
/// </summary>
public enum PeerSource
{
    Unknown = 0,
    PEX = 4,        // Peer Exchange - least trusted (other peers told us)
    DHT = 8,        // Distributed Hash Table
    LSD = 16,       // Local Service Discovery - same network
    Tracker = 32    // Tracker - most trusted (server verified)
}

/// <summary>
/// Helper for peer source ranking operations.
/// </summary>
public static class PeerSourceHelper
{
    public static int GetSourceRank(string source)
    {
        if (string.IsNullOrEmpty(source))
            return (int)PeerSource.Unknown;

        return source.ToLowerInvariant() switch
        {
            "tracker" => (int)PeerSource.Tracker,
            "lsd" or "local" => (int)PeerSource.LSD,
            "dht" => (int)PeerSource.DHT,
            "pex" or "peer_exchange" => (int)PeerSource.PEX,
            _ => (int)PeerSource.Unknown
        };
    }

    public static PeerSource ParseSource(string source)
    {
        if (string.IsNullOrEmpty(source))
            return PeerSource.Unknown;

        return source.ToLowerInvariant() switch
        {
            "tracker" => PeerSource.Tracker,
            "lsd" or "local" => PeerSource.LSD,
            "dht" => PeerSource.DHT,
            "pex" or "peer_exchange" => PeerSource.PEX,
            _ => PeerSource.Unknown
        };
    }
}

/// <summary>
/// Unified peer selection combining priority calculation, connection decisions, and candidate ranking.
///
/// Consolidates three formerly separate classes:
/// - GlobalPeerPriorityCalculator: CRC32-based commutative priority (BEP 40 style)
/// - PriorityBasedPeerSelector: Accept/reject/evict decisions at connection limit
/// - ConnectionCandidateSelector: Local candidate ranking with cache, failcount backoff
///
/// Reference: https://blog.libtorrent.org/2012/12/swarm-connectivity/
/// </summary>
public class PeerSelector : IPeerPriorityCalculator, IPeerSelector
{
    private readonly IPeerRegistry _peerRegistry;
    private readonly ILogger<PeerSelector> _logger;

    // Candidate cache (libtorrent caches top 10)
    private readonly List<PeerState> _candidateCache = new();
    private readonly object _cacheLock = new();
    private DateTime _cacheBuiltAt = DateTime.MinValue;
    private int _roundRobinIndex;

    // Configuration
    private const int MaxCandidateCache = 10;
    private const int MaxIterationsPerScan = 300;
    private const int MaxFailcountForCandidate = 3;
    private const uint PriorityHysteresis = 1000;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BaseReconnectTime = TimeSpan.FromSeconds(60);

    // Local network detection
    private readonly HashSet<string> _localSubnets = new();
    private IPEndPoint _localEndpoint;

    // State
    private bool _isFinished;

    public PeerSelector(
        IPeerRegistry peerRegistry = null,
        ILogger<PeerSelector> logger = null)
    {
        _peerRegistry = peerRegistry;
        _logger = logger;

        DetectLocalSubnets();
    }

    #region IPeerPriorityCalculator - Global priority (commutative, CRC32-based)

    /// <summary>
    /// Calculates a globally-agreed priority for a connection.
    /// Uses XOR of endpoint hashes for commutativity: A^B == B^A.
    /// </summary>
    public uint CalculatePriority(IPEndPoint local, IPEndPoint remote)
    {
        if (local == null || remote == null)
            return 0;

        uint localHash = ComputeEndpointHash(local);
        uint remoteHash = ComputeEndpointHash(remote);
        return localHash ^ remoteHash;
    }

    public int Compare(IPeerConnection a, IPeerConnection b, IPEndPoint localEndpoint)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        uint priorityA = CalculatePriority(localEndpoint, a.PeerInfo?.EndPoint);
        uint priorityB = CalculatePriority(localEndpoint, b.PeerInfo?.EndPoint);
        return priorityA.CompareTo(priorityB);
    }

    public IPeerConnection FindLowestPriority(IEnumerable<IPeerConnection> peers, IPEndPoint localEndpoint)
    {
        if (peers == null || localEndpoint == null)
            return null;

        IPeerConnection lowest = null;
        uint lowestPriority = uint.MaxValue;

        foreach (var peer in peers)
        {
            if (peer?.PeerInfo?.EndPoint == null)
                continue;

            uint priority = CalculatePriority(localEndpoint, peer.PeerInfo.EndPoint);
            if (priority < lowestPriority)
            {
                lowestPriority = priority;
                lowest = peer;
            }
        }

        return lowest;
    }

    public IPeerConnection FindHighestPriority(IEnumerable<IPeerConnection> peers, IPEndPoint localEndpoint)
    {
        if (peers == null || localEndpoint == null)
            return null;

        IPeerConnection highest = null;
        uint highestPriority = 0;

        foreach (var peer in peers)
        {
            if (peer?.PeerInfo?.EndPoint == null)
                continue;

            uint priority = CalculatePriority(localEndpoint, peer.PeerInfo.EndPoint);
            if (priority > highestPriority)
            {
                highestPriority = priority;
                highest = peer;
            }
        }

        return highest;
    }

    private static uint ComputeEndpointHash(IPEndPoint endpoint)
    {
        if (endpoint?.Address == null)
            return 0;

        byte[] ipBytes = endpoint.Address.GetAddressBytes();
        uint hash = Crc32Hash(ipBytes);

        uint portValue = (uint)endpoint.Port;
        hash ^= (portValue << 16) | (portValue >> 16);
        return hash;
    }

    private static uint Crc32Hash(byte[] data)
    {
        const uint polynomial = 0xEDB88320;
        uint crc = 0xFFFFFFFF;

        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ polynomial;
                else
                    crc >>= 1;
            }
        }

        return ~crc;
    }

    #endregion

    #region IPeerSelector - Connection accept/reject/evict decisions

    public IPeerConnection SelectForDisconnection(
        IEnumerable<IPeerConnection> connections,
        IPEndPoint localEndpoint)
    {
        return FindLowestPriority(connections, localEndpoint);
    }

    public ConnectionDecision ShouldAcceptConnection(
        PeerInfo newPeer,
        IEnumerable<IPeerConnection> existingPeers,
        int maxConnections,
        IPEndPoint localEndpoint)
    {
        if (newPeer?.EndPoint == null || localEndpoint == null)
            return ConnectionDecision.Reject("Invalid peer or local endpoint");

        var existing = existingPeers?.ToList() ?? new List<IPeerConnection>();

        if (existing.Count < maxConnections)
        {
            _logger?.LogDebug("Accepting {Peer} - under connection limit ({Count}/{Max})",
                newPeer.EndPoint, existing.Count, maxConnections);
            return ConnectionDecision.Accept($"Under limit ({existing.Count}/{maxConnections})");
        }

        uint newPriority = CalculatePriority(localEndpoint, newPeer.EndPoint);

        var lowest = FindLowestPriority(existing, localEndpoint);
        if (lowest?.PeerInfo?.EndPoint == null)
            return ConnectionDecision.Reject("No valid existing peers to compare");

        uint lowestPriority = CalculatePriority(localEndpoint, lowest.PeerInfo.EndPoint);

        if (newPriority > lowestPriority + PriorityHysteresis)
        {
            _logger?.LogInformation(
                "Accepting {NewPeer} (priority {NewP:X8}) - displacing {OldPeer} (priority {OldP:X8})",
                newPeer.EndPoint, newPriority,
                lowest.PeerInfo.EndPoint, lowestPriority);

            return ConnectionDecision.AcceptAndDisconnect(lowest, newPriority, lowestPriority);
        }

        _logger?.LogDebug(
            "Rejecting {NewPeer} (priority {NewP:X8}) - not higher than lowest {OldP:X8}",
            newPeer.EndPoint, newPriority, lowestPriority);

        return ConnectionDecision.Reject(
            $"Priority {newPriority:X8} not significantly higher than lowest {lowestPriority:X8}");
    }

    public IReadOnlyList<IPeerConnection> SelectToKeep(
        IEnumerable<IPeerConnection> connections,
        int targetCount,
        IPEndPoint localEndpoint)
    {
        if (connections == null || localEndpoint == null)
            return new List<IPeerConnection>();

        return connections
            .Where(p => p?.PeerInfo?.EndPoint != null)
            .OrderByDescending(p => CalculatePriority(localEndpoint, p.PeerInfo.EndPoint))
            .Take(targetCount)
            .ToList();
    }

    #endregion

    #region IConnectionCandidateSelector - Outbound candidate ranking with cache

    public void SetLocalEndpoint(IPEndPoint endpoint)
    {
        _localEndpoint = endpoint;
        InvalidateCache();
    }

    public void SetFinished(bool finished)
    {
        if (_isFinished != finished)
        {
            _isFinished = finished;
            InvalidateCache();
        }
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _candidateCache.Clear();
            _cacheBuiltAt = DateTime.MinValue;
        }
    }

    public PeerState GetNextCandidate()
    {
        lock (_cacheLock)
        {
            if (_candidateCache.Count == 0 || DateTime.UtcNow - _cacheBuiltAt > CacheLifetime)
                RebuildCandidateCache();

            if (_candidateCache.Count > 0)
            {
                var candidate = _candidateCache[0];
                _candidateCache.RemoveAt(0);
                return candidate;
            }

            return null;
        }
    }

    public IReadOnlyList<PeerState> GetCandidates(int count)
    {
        var result = new List<PeerState>();
        for (int i = 0; i < count; i++)
        {
            var candidate = GetNextCandidate();
            if (candidate == null)
                break;
            result.Add(candidate);
        }
        return result;
    }

    public void RecordConnectionResult(PeerInfo peer, bool success)
    {
        if (peer == null || _peerRegistry == null)
            return;

        var key = $"{peer.IpAddress}:{peer.Port}";
        if (success)
            _peerRegistry.RecordConnectionSuccess(key);
        else
            _peerRegistry.RecordConnectionFailure(key);

        InvalidateCache();
    }

    private void RebuildCandidateCache()
    {
        _candidateCache.Clear();

        if (_peerRegistry == null)
            return;

        var allPeers = _peerRegistry.GetPeersWhere(_ => true);
        if (allPeers.Count == 0)
            return;

        int scanned = 0;
        int startIndex = _roundRobinIndex % allPeers.Count;
        int currentIndex = startIndex;

        do
        {
            var peer = allPeers[currentIndex];
            if (IsValidCandidate(peer))
                InsertCandidate(peer);

            currentIndex = (currentIndex + 1) % allPeers.Count;
            scanned++;
        } while (currentIndex != startIndex && scanned < MaxIterationsPerScan);

        _roundRobinIndex = currentIndex;
        _cacheBuiltAt = DateTime.UtcNow;

        _logger?.LogDebug(
            "Rebuilt candidate cache: {Count} candidates from {Scanned}/{Total} peers scanned",
            _candidateCache.Count, scanned, allPeers.Count);
    }

    private bool IsValidCandidate(PeerState peer)
    {
        if (peer?.Info == null)
            return false;

        if (peer.Status == PeerConnectionStatus.Connected ||
            peer.Status == PeerConnectionStatus.Connecting)
            return false;

        if (peer.Status == PeerConnectionStatus.Banned)
            return false;

        int failcount = peer.Score?.FailedConnections ?? 0;
        if (failcount >= MaxFailcountForCandidate)
            return false;

        if (peer.LastConnectedAt.HasValue)
        {
            var minWait = TimeSpan.FromTicks(BaseReconnectTime.Ticks * (failcount + 1));
            var elapsed = DateTime.UtcNow - peer.LastConnectedAt.Value;
            if (elapsed < minWait)
                return false;
        }

        if (_isFinished && peer.Info.IsSeed)
            return false;

        return true;
    }

    private void InsertCandidate(PeerState candidate)
    {
        int low = 0;
        int high = _candidateCache.Count;

        while (low < high)
        {
            int mid = (low + high) / 2;
            if (ComparePeers(candidate, _candidateCache[mid]) < 0)
                high = mid;
            else
                low = mid + 1;
        }

        if (_candidateCache.Count < MaxCandidateCache)
        {
            _candidateCache.Insert(low, candidate);
        }
        else if (low < MaxCandidateCache)
        {
            _candidateCache.Insert(low, candidate);
            _candidateCache.RemoveAt(MaxCandidateCache);
        }
    }

    /// <summary>
    /// Compares two peers for connection priority using libtorrent's cascading comparison:
    /// failcount -> local network -> last connected -> seed status -> source rank -> BEP 40 priority.
    /// </summary>
    public int ComparePeers(PeerState a, PeerState b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return 1;
        if (b == null) return -1;

        int failA = a.Score?.FailedConnections ?? 0;
        int failB = b.Score?.FailedConnections ?? 0;
        if (failA != failB)
            return failA.CompareTo(failB);

        bool localA = IsLocalPeer(a.Info);
        bool localB = IsLocalPeer(b.Info);
        if (localA != localB)
            return localA ? -1 : 1;

        var lastConnA = a.LastConnectedAt ?? DateTime.MinValue;
        var lastConnB = b.LastConnectedAt ?? DateTime.MinValue;
        if (lastConnA != lastConnB)
            return lastConnA.CompareTo(lastConnB);

        if (!_isFinished)
        {
            bool seedA = a.Info?.IsSeed ?? false;
            bool seedB = b.Info?.IsSeed ?? false;
            if (seedA != seedB)
                return seedA ? 1 : -1;
        }

        int sourceRankA = PeerSourceHelper.GetSourceRank(a.Info?.Source);
        int sourceRankB = PeerSourceHelper.GetSourceRank(b.Info?.Source);
        if (sourceRankA != sourceRankB)
            return sourceRankB.CompareTo(sourceRankA);

        if (_localEndpoint != null && a.Info?.EndPoint != null && b.Info?.EndPoint != null)
        {
            uint rankA = CalculatePriority(_localEndpoint, a.Info.EndPoint);
            uint rankB = CalculatePriority(_localEndpoint, b.Info.EndPoint);
            if (rankA != rankB)
                return rankB.CompareTo(rankA);
        }

        return 0;
    }

    #endregion

    #region Network detection helpers

    private bool IsLocalPeer(PeerInfo peer)
    {
        if (peer?.IpAddress == null)
            return false;

        if (IPAddress.IsLoopback(peer.IpAddress))
            return true;

        if (IsPrivateAddress(peer.IpAddress))
            return true;

        string peerSubnet = GetSubnetPrefix(peer.IpAddress);
        return _localSubnets.Contains(peerSubnet);
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal) return true;
            byte[] bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
        }

        return false;
    }

    private static string GetSubnetPrefix(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();
            return $"{bytes[0]:X2}{bytes[1]:X2}:{bytes[2]:X2}{bytes[3]:X2}:{bytes[4]:X2}{bytes[5]:X2}:{bytes[6]:X2}{bytes[7]:X2}";
        }
        return address.ToString();
    }

    private void DetectLocalSubnets()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                var ipProps = nic.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork ||
                        addr.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        _localSubnets.Add(GetSubnetPrefix(addr.Address));
                    }
                }
            }

            _logger?.LogDebug("Detected {Count} local subnets", _localSubnets.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to detect local subnets");
        }
    }

    #endregion
}
