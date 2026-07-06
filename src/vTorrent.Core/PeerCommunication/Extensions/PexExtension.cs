using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Bencode.Objects;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.PeerCommunication.Extensions;

/// <summary>
/// Implements the ut_pex (Peer Exchange) extension defined by BEP 11,
/// transported over the BEP 10 extension protocol.
/// Allows peers to exchange peer lists, reducing reliance on trackers.
/// </summary>
public class PexExtension : IExtension
{
    private readonly ILogger<PexExtension> _logger;
    private readonly Func<IReadOnlyList<PexPeerInfo>> _getPeersFunc;
    private readonly Action<IEnumerable<PexPeerEntry>> _onPeersDiscovered;
    private readonly bool _isPrivateTorrent;

    private readonly HashSet<IPEndPoint> _previouslySentPeers = new();
    private readonly HashSet<IPEndPoint> _receivedPeers = new();
    private readonly object _lock = new();

    private DateTime _lastPexSent = DateTime.MinValue;
    private bool _firstMessage = true;

    // Rate limiting for incoming PEX messages (from libtorrent)
    private readonly DateTime[] _lastPexReceived = new DateTime[6];
    private int _pexReceivedIndex = 0;

    /// <summary>
    /// PEX message interval (60 seconds as per libtorrent).
    /// </summary>
    public static readonly TimeSpan PexInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum peers to accept from a single PEX message.
    /// </summary>
    public const int MaxPexPeers = 100;

    public string Name => "ut_pex";
    public byte LocalExtensionId { get; } = 1;
    public byte? RemoteExtensionId { get; set; }
    public bool IsEnabled => !_isPrivateTorrent;

    public PexExtension(
        ILogger<PexExtension> logger,
        Func<IReadOnlyList<PexPeerInfo>> getPeersFunc,
        Action<IEnumerable<PexPeerEntry>> onPeersDiscovered,
        bool isPrivateTorrent = false)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _getPeersFunc = getPeersFunc ?? throw new ArgumentNullException(nameof(getPeersFunc));
        _onPeersDiscovered = onPeersDiscovered ?? throw new ArgumentNullException(nameof(onPeersDiscovered));
        _isPrivateTorrent = isPrivateTorrent;

        // Initialize rate limiting timestamps
        for (int i = 0; i < _lastPexReceived.Length; i++)
            _lastPexReceived[i] = DateTime.MinValue;
    }

    public Task OnExtensionHandshakeReceivedAsync(BDictionary handshake)
    {
        // Extract the remote extension ID from the "m" dictionary
        if (handshake.TryGetValue("m", out var mObj) && mObj is BDictionary mDict)
        {
            if (mDict.TryGetValue(Name, out var idObj) && idObj is BNumber idNum)
            {
                RemoteExtensionId = (byte)idNum.Value;
                _logger.LogDebug("Peer supports {Extension} with ID {Id}", Name, RemoteExtensionId);
            }
        }

        return Task.CompletedTask;
    }

    public void AddToHandshake(BDictionary handshake)
    {
        if (!IsEnabled)
            return;

        // Ensure "m" dictionary exists
        if (!handshake.TryGetValue("m", out var mObj) || mObj is not BDictionary mDict)
        {
            mDict = new BDictionary();
            handshake.Add("m", mDict);
        }

        // Add our extension ID
        mDict.AddNumber(Name, LocalExtensionId);
    }

    public async Task<byte[]> GenerateMessageAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || RemoteExtensionId == null)
            return null;

        // Check if enough time has passed since last PEX message
        var now = DateTime.UtcNow;
        if (now - _lastPexSent < PexInterval && !_firstMessage)
            return null;

        try
        {
            var currentPeers = _getPeersFunc()
                .Where(p => IsValidPexPeer(p.EndPoint) && !p.IsPrivate)
                .Take(PexMessage.MaxPeerEntries)
                .ToList();

            var currentEndpoints = currentPeers.Select(p => p.EndPoint).ToHashSet();

            List<IPEndPoint> added;
            List<PexFlags> addedFlags;
            List<IPEndPoint> dropped;

            lock (_lock)
            {
                if (_firstMessage)
                {
                    // First message: send all current peers
                    added = currentPeers.Select(p => p.EndPoint).ToList();
                    addedFlags = currentPeers.Select(p => p.Flags).ToList();
                    dropped = new List<IPEndPoint>();
                }
                else
                {
                    // Subsequent messages: send only changes
                    added = currentEndpoints.Except(_previouslySentPeers).ToList();
                    addedFlags = added.Select(ep =>
                    {
                        var peer = currentPeers.FirstOrDefault(p => p.EndPoint.Equals(ep));
                        return peer?.Flags ?? PexFlags.None;
                    }).ToList();
                    dropped = _previouslySentPeers.Except(currentEndpoints).ToList();
                }

                // Limit to max entries
                if (added.Count > PexMessage.MaxPeerEntries)
                {
                    added = added.Take(PexMessage.MaxPeerEntries).ToList();
                    addedFlags = addedFlags.Take(PexMessage.MaxPeerEntries).ToList();
                }
                if (dropped.Count > PexMessage.MaxPeerEntries)
                    dropped = dropped.Take(PexMessage.MaxPeerEntries).ToList();

                // No changes and not first message
                if (added.Count == 0 && dropped.Count == 0 && !_firstMessage)
                    return null;

                // Update our tracking
                _previouslySentPeers.Clear();
                foreach (var ep in currentEndpoints.Take(PexMessage.MaxPeerEntries))
                    _previouslySentPeers.Add(ep);

                _lastPexSent = now;
                _firstMessage = false;
            }

            // Separate IPv4 and IPv6
            var message = new PexMessage();

            for (int i = 0; i < added.Count; i++)
            {
                var ep = added[i];
                var flags = addedFlags[i];

                if (ep.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    message.Added.Add(ep);
                    message.AddedFlags.Add(flags);
                }
                else if (ep.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    message.Added6.Add(ep);
                    message.Added6Flags.Add(flags);
                }
            }

            foreach (var ep in dropped)
            {
                if (ep.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    message.Dropped.Add(ep);
                else if (ep.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    message.Dropped6.Add(ep);
            }

            _logger.LogDebug("Generating PEX message: {Message}", message);
            return message.Encode();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error generating PEX message");
            return null;
        }
    }

    public Task OnMessageReceivedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return Task.CompletedTask;

        try
        {
            // Rate limiting check (prevent PEX flooding)
            var now = DateTime.UtcNow;
            if (now - _lastPexReceived[0] < TimeSpan.FromSeconds(60))
            {
                _logger.LogDebug("PEX message rate limit exceeded, ignoring");
                return Task.CompletedTask;
            }

            // Shift rate limiting array
            for (int i = 0; i < _lastPexReceived.Length - 1; i++)
                _lastPexReceived[i] = _lastPexReceived[i + 1];
            _lastPexReceived[_lastPexReceived.Length - 1] = now;

            // Parse the message
            var message = PexMessage.Parse(payload.Span);

            _logger.LogDebug("Received PEX message: {Message}", message);

            // Collect valid new peers
            var newPeers = new List<PexPeerEntry>();

            // Process IPv4 peers
            for (int i = 0; i < message.Added.Count && newPeers.Count < MaxPexPeers; i++)
            {
                var ep = message.Added[i];
                var flags = i < message.AddedFlags.Count ? message.AddedFlags[i] : PexFlags.None;

                if (IsValidPexPeer(ep) && !IsAlreadyKnown(ep))
                {
                    newPeers.Add(new PexPeerEntry(ep, flags));
                    lock (_lock) _receivedPeers.Add(ep);
                }
            }

            // Process IPv6 peers
            for (int i = 0; i < message.Added6.Count && newPeers.Count < MaxPexPeers; i++)
            {
                var ep = message.Added6[i];
                var flags = i < message.Added6Flags.Count ? message.Added6Flags[i] : PexFlags.None;

                if (IsValidPexPeer(ep) && !IsAlreadyKnown(ep))
                {
                    newPeers.Add(new PexPeerEntry(ep, flags));
                    lock (_lock) _receivedPeers.Add(ep);
                }
            }

            // Remove dropped peers from our tracking
            lock (_lock)
            {
                foreach (var ep in message.Dropped)
                    _receivedPeers.Remove(ep);
                foreach (var ep in message.Dropped6)
                    _receivedPeers.Remove(ep);
            }

            // Notify about new peers
            if (newPeers.Count > 0)
            {
                _logger.LogDebug("Discovered {Count} new peers via PEX", newPeers.Count);
                _onPeersDiscovered(newPeers);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error processing PEX message");
        }

        return Task.CompletedTask;
    }

    public Task OnConnectedAsync(CancellationToken cancellationToken = default)
    {
        // Reset state for new connection
        lock (_lock)
        {
            _previouslySentPeers.Clear();
            _firstMessage = true;
            _lastPexSent = DateTime.MinValue;
        }

        return Task.CompletedTask;
    }

    public Task OnDisconnectingAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    private bool IsAlreadyKnown(IPEndPoint endpoint)
    {
        lock (_lock)
        {
            return _receivedPeers.Contains(endpoint);
        }
    }

    /// <summary>
    /// Checks if this peer has reported knowing about the given endpoint via PEX.
    /// Used by HolepunchManager to find relay candidates.
    /// NOTE: This is a heuristic — PEX data means the peer has seen the target,
    /// not a guarantee of an active connection.
    /// </summary>
    public bool KnowsPeer(IPEndPoint endpoint)
    {
        lock (_lock)
        {
            return _receivedPeers.Contains(endpoint);
        }
    }

    /// <summary>
    /// Validates that a peer endpoint is suitable for PEX sharing.
    /// Matches libtorrent behavior: private IPs are valid (LAN peers).
    /// Only rejects loopback and zero addresses.
    /// </summary>
    private static bool IsValidPexPeer(IPEndPoint endpoint)
    {
        if (endpoint == null)
            return false;

        var addr = endpoint.Address;

        // Reject invalid ports
        if (endpoint.Port <= 0 || endpoint.Port > 65535)
            return false;

        // Reject loopback
        if (IPAddress.IsLoopback(addr))
            return false;

        // For IPv4: reject 0.0.0.0
        if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
            addr.IsIPv4MappedToIPv6)
        {
            var ipv4 = addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr;
            var bytes = ipv4.GetAddressBytes();

            if (bytes[0] == 0)
                return false;
        }

        return true;
    }
}

/// <summary>
/// Information about a peer for PEX message generation.
/// </summary>
public class PexPeerInfo
{
    public IPEndPoint EndPoint { get; init; }
    public PexFlags Flags { get; init; }
    public bool IsPrivate { get; init; }
    public bool IsOutgoing { get; init; }
    public bool IsConnected { get; init; }

    public PexPeerInfo(IPEndPoint endPoint, PexFlags flags = PexFlags.None, bool isPrivate = false)
    {
        EndPoint = endPoint;
        Flags = flags;
        IsPrivate = isPrivate;
    }
}
