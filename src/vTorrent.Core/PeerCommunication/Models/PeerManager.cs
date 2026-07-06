using Microsoft.Extensions.Logging;

using System;

using System.Collections.Concurrent;

using System.Collections.Generic;

using System.IO;

using System.Linq;

using System.Net;

using System.Net.Sockets;

using System.Text;

using System.Threading;

using System.Threading.Tasks;

using vTorrent.Core;

using vTorrent.Core.Interfaces;

using vTorrent.Core.PeerCommunication.Bandwidth;

using vTorrent.Core.PeerCommunication;

using vTorrent.Core.PeerCommunication.Encryption;

using vTorrent.Core.PeerCommunication.Events;

using vTorrent.Core.PeerCommunication.Transport;

using vTorrent.Core.PeerCommunication.Transport.Tcp;

using vTorrent.Abstractions.Interfaces.Transport;

using vTorrent.Abstractions.Models;

using vTorrent.Abstractions.Settings;

using vTorrent.Abstractions.Enums;

using vTorrent.Abstractions.Interfaces;
using vTorrent.Abstractions.Settings.Enums;
using vTorrent.Core.Engine;
using vTorrent.Core.Upload;
using vTorrent.Core.Network.PeerClass;
using Microsoft.Extensions.Options;

using PeerSettings = vTorrent.Abstractions.Settings.PeerSettings;

namespace vTorrent.Core.PeerCommunication.Models;

/// <summary>

/// Manages peer connections with priority-based connection management.

///

/// Key improvements from libtorrent analysis:

/// 1. Priority-based replacement instead of first-come-first-serve

/// 2. Global peer priority for consistent swarm connectivity

/// 3. Fail count tracking for better peer selection

/// 4. Local peer preference

///

/// Reference: https://blog.libtorrent.org/2012/12/swarm-connectivity/

/// </summary>

public class PeerManager : IPeerManager

{

    private readonly IExternalIpVoter? _externalIpVoter;

    private readonly ConnectionSettings? _connectionSettings;

    private readonly PeerSettings _settings;

    private readonly ILogger<PeerManager> _logger;

    private readonly ILoggerFactory _loggerFactory;

    private readonly byte[] _infoHash;

    private readonly IStatisticsTracker _statisticsTracker;

    private readonly PeerRegistry _peerRegistry;

    private readonly IPeerPriorityCalculator _priorityCalculator;

    private readonly IPeerBandwidthLimiter _bandwidthLimiter;

    private readonly ITransportConnector _transportConnector;

    // MSE/PE encryption

    private readonly IOptionsMonitor<EncryptionSettings> _encryptionMonitor;

    private readonly IOptionsMonitor<PrivacySettings> _privacyMonitor;

    private readonly IOptionsMonitor<ConnectionSettings>? _connectionMonitor;

    // Rate limiting for outgoing connection attempts (ConnectionSpeed).
    private int _connectionsThisTick;
    private DateTime _lastTickReset = DateTime.UtcNow;

    private readonly ILogger<Encryption.MseNegotiator> _mseLogger;

    private readonly PeerClassManager? _peerClassManager;

    // Connection management - no longer using semaphore, using priority-based replacement

    private readonly object _connectionLock = new();

    private IPEndPoint _localEndpoint;

    // Local bitfield provider for sending bitfield after handshake

    private Func<byte[]?>? _localBitfieldProvider;

    // BEP 52 hash exchange handler

    private IHashExchangeHandler? _hashExchangeHandler;

    // BEP 54: per-peer extension setup callback (called before handshake)

    private Action<IPeerConnection>? _peerExtensionSetup;

    private readonly bool _isI2pTorrent;
    private readonly bool _allowMixedMode;

    // Seeding state for redundant connection handling

    private bool _isSeeding;

    private bool _closeRedundantConnections = true;

    private bool _seedingOutgoingConnections = true;

    private bool _isRunning;

    private CancellationTokenSource _stopCts = new();

    // libtorrent parity (session on_tick → connect_more_peers): periodic top-up of

    // outgoing connections from the registry. Without it, reconnects depend entirely

    // on new peer-add events (tracker/DHT/PEX) and known-but-disconnected peers are

    // never retried — after a resume with dead trackers the swarm never refills.

    private Timer? _connectTopUpTimer;

    // Single-flight gate for the connect-attempt drain loop: the resume-time boost

    // and the top-up timer must not run two loops over the same candidates.

    private int _connectBoostRunning;

    public int ConnectedPeerCount => _peerRegistry.ConnectedPeerCount;

    public int MaxConnections => _settings.MaxConnections;

    public IReadOnlyList<IPeerConnection> ConnectedPeers => _peerRegistry.GetAllConnectedPeers();

    public byte[] InfoHash => _infoHash;

    public long TotalBytesDownloaded => ConnectedPeers.Sum(p => p.BytesDownloaded);

    public long TotalBytesUploaded => ConnectedPeers.Sum(p => p.BytesUploaded);

    public bool SuperSeedingActive { get; set; }

    public event EventHandler<PeerConnectedEventArgs> PeerConnected;

    public event EventHandler<PeerDisconnectedEventArgs> PeerDisconnected;

    public event EventHandler<PeerMessageEventArgs> MessageReceived;

    public PeerManager(

        byte[] infoHash,

        PeerSettings settings,

        ILoggerFactory loggerFactory,

        PeerRegistry peerRegistry,

        ITransportConnector transportConnector,

        IStatisticsTracker statisticsTracker = null,

        IPeerPriorityCalculator priorityCalculator = null,

        IPeerBandwidthLimiter bandwidthLimiter = null,

        IOptionsMonitor<EncryptionSettings>? encryptionMonitor = null,

        IExternalIpVoter? externalIpVoter = null,

        ConnectionSettings? connectionSettings = null,

        IOptionsMonitor<PrivacySettings>? privacyMonitor = null,

        PeerClassManager? peerClassManager = null,

        IOptionsMonitor<ConnectionSettings>? connectionMonitor = null,

        bool isI2pTorrent = false,

        bool allowMixedMode = false)

    {

        if (infoHash == null || infoHash.Length != 20)

            throw new ArgumentException("InfoHash must be exactly 20 bytes");

        _infoHash = infoHash;

        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

        _logger = loggerFactory.CreateLogger<PeerManager>();

        _peerRegistry = peerRegistry ?? throw new ArgumentNullException(nameof(peerRegistry));

        _transportConnector = transportConnector ?? throw new ArgumentNullException(nameof(transportConnector));

        _statisticsTracker = statisticsTracker;

        _bandwidthLimiter = bandwidthLimiter;

        _encryptionMonitor = encryptionMonitor ?? new OptionsMonitorShim<EncryptionSettings>(new EncryptionSettings());

        _externalIpVoter = externalIpVoter;

        _connectionSettings = connectionSettings;

        _privacyMonitor = privacyMonitor;

        _peerClassManager = peerClassManager;

        _connectionMonitor = connectionMonitor;

        _isI2pTorrent = isI2pTorrent;

        _allowMixedMode = allowMixedMode;

        _mseLogger = loggerFactory.CreateLogger<Encryption.MseNegotiator>();

        // Use provided calculator or create default

        _priorityCalculator = priorityCalculator ?? new PeerSelector();

        // Default local endpoint - will be updated when we know our actual endpoint

        _localEndpoint = new IPEndPoint(IPAddress.Any, settings.ListenPort);

    }

    /// <summary>

    /// Sets the local bitfield provider for sending bitfield after handshake.

    /// Called by TorrentEngine after DownloadCoordinator is created.

    /// </summary>

    public void SetLocalBitfieldProvider(Func<byte[]?> provider)

    {

        _localBitfieldProvider = provider;

    }

    /// <summary>

    /// Sets the local endpoint for priority calculations.

    /// Should be called when the actual listening endpoint is known.

    /// </summary>

    public void SetLocalEndpoint(IPEndPoint endpoint)

    {

        _localEndpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

        _logger.LogDebug("Local endpoint set to {Endpoint}", endpoint);

    }

    /// <summary>BEP 52: Set handler for hash exchange messages on all new connections.</summary>

    public void SetHashExchangeHandler(IHashExchangeHandler? handler)

    {

        _hashExchangeHandler = handler;

    }

    /// <summary>BEP 54: Set a callback invoked on each new PeerConnection before the handshake, for extension registration.</summary>

    public void SetPeerExtensionSetup(Action<IPeerConnection>? callback)

    {

        _peerExtensionSetup = callback;

    }

    private bool ShouldAttemptMse(PeerInfo peerInfo, bool outbound)

    {

        if (peerInfo.IsI2p) return false;

        var policy = outbound ? _encryptionMonitor.CurrentValue.OutPolicy : _encryptionMonitor.CurrentValue.InPolicy;

        if (policy == EncryptionPolicy.Disabled) return false;

        if (policy == EncryptionPolicy.Forced) return true;

        return peerInfo.EncryptionSupport != MsePeerEncryptionSupport.Unsupported;

    }

    /// <summary>

    /// Sets whether we are in seeding mode.

    /// When seeding, redundant seed-to-seed connections may be closed.

    /// </summary>

    public void SetSeeding(bool isSeeding)

    {

        if (_isSeeding != isSeeding)

        {

            _isSeeding = isSeeding;

            _logger.LogDebug("Seeding mode set to {IsSeeding}", isSeeding);

            // Check for redundant connections when transitioning to seeding

            if (isSeeding && _closeRedundantConnections)

            {

                _ = CloseRedundantSeedConnectionsAsync();

            }

        }

    }

    /// <summary>

    /// Configures whether to close redundant seed-to-seed connections.

    /// </summary>

    public void SetCloseRedundantConnections(bool close)

    {

        _closeRedundantConnections = close;

    }

    /// <summary>

    /// Configures whether outgoing connections are allowed while seeding.

    /// When false, no new outgoing peer connections are attempted during seeding.

    /// </summary>

    public void SetSeedingOutgoingConnections(bool allow)

    {

        _seedingOutgoingConnections = allow;

    }

    /// <summary>

    /// Closes redundant seed-to-seed connections.

    /// When both we and the peer are seeds, there's no point in maintaining the connection.

    /// Reference: libtorrent's close_redundant_connections setting

    /// </summary>

    public async Task CloseRedundantSeedConnectionsAsync()

    {

        if (!_isSeeding || !_closeRedundantConnections)

            return;

        var connectedPeers = _peerRegistry.GetAllConnectedPeers().ToList();

        var redundantPeers = new List<IPeerConnection>();

        foreach (var peer in connectedPeers)

        {

            // Check if peer is a seed (has 100% of the torrent)

            bool peerIsSeed = peer.PeerInfo?.IsSeed ?? false;

            // If peer bitfield is complete, they're a seed

            if (!peerIsSeed && peer.PeerBitfield != null)

            {

                // Check if all bits are set in the bitfield

                peerIsSeed = IsBitfieldComplete(peer.PeerBitfield);

            }

            if (peerIsSeed)

            {

                _logger.LogDebug("Found redundant seed-to-seed connection: {Peer}", peer.PeerInfo?.EndPoint);

                redundantPeers.Add(peer);

            }

        }

        if (redundantPeers.Any())

        {

            _logger.LogDebug("Closing {Count} redundant seed-to-seed connections", redundantPeers.Count);

            foreach (var peer in redundantPeers)

            {

                try

                {

                    await RemovePeerAsync(peer).ConfigureAwait(false);

                }

                catch (Exception ex)

                {

                    _logger.LogDebug(ex, "Error closing redundant connection to {Peer}", peer.PeerInfo?.EndPoint);

                }

            }

        }

    }

    /// <summary>

    /// Checks if a single peer connection is redundant (seed-to-seed).

    /// Call this when a peer becomes a seed (receives "have all" or completes their download).

    /// </summary>

    public async Task CheckAndCloseIfRedundantAsync(IPeerConnection peer)

    {

        if (!_isSeeding || !_closeRedundantConnections || peer == null)

            return;

        bool peerIsSeed = peer.PeerInfo?.IsSeed ?? false;

        if (!peerIsSeed && peer.PeerBitfield != null)

        {

            peerIsSeed = IsBitfieldComplete(peer.PeerBitfield);

        }

        if (peerIsSeed)

        {

            _logger.LogDebug("Closing redundant seed-to-seed connection: {Peer}", peer.PeerInfo?.EndPoint);

            await RemovePeerAsync(peer).ConfigureAwait(false);

        }

    }

    /// <summary>

    /// Checks if a bitfield indicates 100% completion (all bits set).

    /// </summary>

    private static bool IsBitfieldComplete(byte[] bitfield)

    {

        if (bitfield == null || bitfield.Length == 0)

            return false;

        // Check all complete bytes

        for (int i = 0; i < bitfield.Length - 1; i++)

        {

            if (bitfield[i] != 0xFF)

                return false;

        }

        // The last byte might have trailing zero bits (for padding)

        // We consider it complete if all meaningful bits are set

        // For simplicity, check if last byte is >= 0x80 (at least MSB is set)

        // A more accurate check would require knowing the actual piece count

        return bitfield[^1] >= 0x80;

    }

    public Task StartAsync(CancellationToken cancellationToken = default)

    {

        if (_isRunning)

            throw new InvalidOperationException("PeerManager is already running");

        _isRunning = true;

        // Create fresh CTS for this run (previous one may have been cancelled on pause)

        _stopCts = new CancellationTokenSource();

        _connectTopUpTimer = new Timer(

            _ => TriggerConnectionAttempts(), null,

            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        _logger.LogDebug(
            "PeerManager started (MaxConnections: {MaxConnections}, Priority-based replacement: enabled)",
            _settings.MaxConnections);

        return Task.CompletedTask;

    }

    public async Task StopAsync()

    {

        if (!_isRunning)

            return;

        _logger.LogDebug("Stopping PeerManager, disconnecting {Count} peers", ConnectedPeerCount);

        _isRunning = false;

        _stopCts.Cancel();

        _connectTopUpTimer?.Dispose();

        _connectTopUpTimer = null;

        // Disconnect all peers but preserve registry for resume

        var connectedPeers = _peerRegistry.GetAllConnectedPeers();

        foreach (var peer in connectedPeers)

        {

            string key = PeerRegistry.GetPeerKey(peer.PeerInfo);

            _peerRegistry.UpdateConnection(key, null, PeerConnectionStatus.Disconnected);

        }

        var disconnectTasks = connectedPeers.Select(peer => DisconnectPeerAsync(peer));

        await Task.WhenAll(disconnectTasks).ConfigureAwait(false);

        _logger.LogDebug("PeerManager stopped");

    }

    /// <summary>
    /// Attach seam for a session-level inbound dispatcher: accepts an already-connected
    /// (and, if applicable, already MSE-negotiated) transport stream for an incoming peer,
    /// completes the BitTorrent handshake, sends our bitfield, and registers the peer.
    /// Performs the max-connections and duplicate-IP checks that used to live in the
    /// per-manager accept loop; on early rejection, disposes <paramref name="effectiveStream"/>.
    /// </summary>
    public async Task AcceptIncomingPeerAsync(ITransportStream effectiveStream, IPEndPoint remote, bool isEncrypted, byte[]? preReadHandshake, CancellationToken ct)

    {

        if (ConnectedPeerCount >= _settings.MaxConnections)

        {

            _logger.LogDebug("Rejecting incoming connection — at max connections ({Max})", _settings.MaxConnections);

            effectiveStream.Dispose();

            return;

        }

        try

        {

            var peerInfo = PeerInfo.Incoming(remote);

            // Check duplicate IP unless AllowMultipleConnectionsPerIp is set

            if ((_connectionMonitor?.CurrentValue ?? _connectionSettings)?.AllowMultipleConnectionsPerIp != true)

            {

                var existingPeer = _peerRegistry.GetAllConnectedPeers()

                    .FirstOrDefault(p => p.PeerInfo.EndPoint.Address.Equals(peerInfo.EndPoint.Address));

                if (existingPeer != null)

                {

                    _logger.LogDebug("Incoming peer {Peer} rejected - duplicate IP (already connected via {Existing})",

                        peerInfo.EndPoint, existingPeer.PeerInfo.EndPoint);

                    effectiveStream.Dispose();

                    return;

                }

            }

            var peerLogger = _loggerFactory.CreateLogger<PeerConnection>();

            // Classify incoming peer and get class-aware bandwidth limiter
            var effectiveLimiter = GetClassAwareLimiter(peerInfo);

            var connection = new PeerConnection(

                peerInfo,

                _settings,

                effectiveStream,

                peerLogger,

                onBytesDownloaded: _statisticsTracker != null

                    ? (peer, bytes) => _statisticsTracker.RecordDownload(peer, bytes)

                    : null,

                onBytesUploaded: _statisticsTracker != null

                    ? (peer, bytes) => _statisticsTracker.RecordUpload(peer, bytes)

                    : null,

                onPayloadDownloaded: null,

                onPayloadUploaded: _statisticsTracker != null

                    ? (peer, bytes) => _statisticsTracker.RecordPayloadUpload(peer, bytes)

                    : null,

                loggerFactory: _loggerFactory,

                bandwidthLimiter: effectiveLimiter,

                externalIpVoter: _externalIpVoter,

                privacyMonitor: _privacyMonitor);

            // Mark as incoming — peer connected to us

            connection.IsIncoming = true;

            connection.IsEncrypted = isEncrypted;

            if (_hashExchangeHandler != null)

                connection.HashExchangeHandler = _hashExchangeHandler;

            _peerExtensionSetup?.Invoke(connection);

            connection.MessageReceived += OnPeerMessageReceived;

            connection.ConnectionLost += (s, e) => _ = HandleConnectionLostAsync(connection, e.Reason);

            // Perform incoming handshake

            await connection.ConnectAsync(_infoHash, ct, preReadHandshake).ConfigureAwait(false);

            // Send our bitfield

            if (_localBitfieldProvider != null)

            {

                try

                {

                    var bitfield = _localBitfieldProvider();

                    if (bitfield != null && bitfield.Length > 0)

                        await connection.SendBitfieldAsync(bitfield, ct).ConfigureAwait(false);

                }

                catch (Exception ex)

                {

                    _logger.LogDebug(ex, "Failed to send bitfield to incoming peer {Peer}", peerInfo.EndPoint);

                }

            }

            // Register in peer registry

            var key = PeerRegistry.GetPeerKey(peerInfo);

            _peerRegistry.GetOrRegister(peerInfo);

            _peerRegistry.UpdateConnection(key, connection, PeerConnectionStatus.Connected);

            _logger.LogDebug("Accepted incoming {Transport} peer {Peer} [Encrypted={Encrypted}] ({Count}/{Max})",

                effectiveStream.TransportType, peerInfo.EndPoint, isEncrypted, ConnectedPeerCount, _settings.MaxConnections);

            PeerConnected?.Invoke(this, new PeerConnectedEventArgs(connection));

        }

        catch (Exception ex)

        {

            _logger.LogDebug(ex, "Failed incoming connection from {Endpoint}", remote);

            effectiveStream.Dispose();

        }

    }

    public async Task<bool> AddPeerAsync(PeerInfo peerInfo, CancellationToken cancellationToken = default)

    {

        if (peerInfo == null)

            throw new ArgumentNullException(nameof(peerInfo));

        if (!_isRunning)

        {

            _logger.LogDebug("Cannot add peer {Peer} - PeerManager not running", peerInfo.EndPoint);

            return false;

        }

        string key = PeerRegistry.GetPeerKey(peerInfo);

        // Get or register peer state

        var peerState = _peerRegistry.GetOrRegister(peerInfo);

        // Peer list is full — reject new peer
        if (peerState == null)
        {
            _logger.LogDebug("Peer list full, rejecting {Peer}", peerInfo.EndPoint);
            return false;
        }

        // Check if already connected or mid-dial (cheap pre-filter; the atomic

        // claim below is the authority)

        if (peerState.Status is PeerConnectionStatus.Connected or PeerConnectionStatus.Connecting)

        {

            _logger.LogDebug("Peer {Peer} already connected/connecting", peerInfo.EndPoint);

            return false;

        }

        // Check if banned

        if (_peerRegistry.IsBanned(key))

        {

            _logger.LogDebug("Peer {Peer} is banned", peerInfo.EndPoint);

            return false;

        }

        // Mixed mode enforcement: reject clearnet peers on pure I2P torrents
        if (_isI2pTorrent && !peerInfo.IsI2p && !_allowMixedMode)
        {
            _logger.LogDebug("Rejected clearnet peer {Peer} on pure I2P torrent", peerInfo.EndPoint);
            return false;
        }

        // Check duplicate IP unless AllowMultipleConnectionsPerIp is set

        if ((_connectionMonitor?.CurrentValue ?? _connectionSettings)?.AllowMultipleConnectionsPerIp != true)

        {

            var existingPeer = _peerRegistry.GetAllConnectedPeers()

                .FirstOrDefault(p => p.PeerInfo.EndPoint.Address.Equals(peerInfo.EndPoint.Address));

            if (existingPeer != null)

            {

                _logger.LogDebug("Peer {Peer} rejected - duplicate IP (already connected via {Existing})",

                    peerInfo.EndPoint, existingPeer.PeerInfo.EndPoint);

                return false;

            }

        }

        // Priority-based connection management

        IPeerConnection peerToDisconnect = null;

        bool shouldConnect = false;

        lock (_connectionLock)

        {

            if (ConnectedPeerCount < _settings.MaxConnections)

            {

                // We have room, allow connection

                shouldConnect = true;

            }

            else

            {

                // At max connections - check if new peer has higher priority

                shouldConnect = TryFindPeerToReplace(peerInfo, out peerToDisconnect);

            }

        }

        if (!shouldConnect)

        {

            _logger.LogDebug("Peer {Peer} rejected - lower priority than existing peers", peerInfo.EndPoint);

            return false;

        }

        // Disconnect lower-priority peer if needed

        if (peerToDisconnect != null)

        {

            _logger.LogDebug("Replacing peer {Old} (lower priority) with {New}",

                peerToDisconnect.PeerInfo.EndPoint, peerInfo.EndPoint);

            await RemovePeerAsync(peerToDisconnect).ConfigureAwait(false);

        }

        try

        {

            // Atomic dial claim — a concurrent peer-add path that already began

            // connecting this endpoint loses here instead of double-dialing.

            if (!_peerRegistry.TryBeginConnecting(key))

            {

                _logger.LogDebug("Peer {Peer} already connecting/connected — skipping duplicate dial", peerInfo.EndPoint);

                return false;

            }

            // Create connection

            var connection = await CreateAndConnectPeerAsync(peerInfo, cancellationToken).ConfigureAwait(false);

            // Update registry with connected peer

            _peerRegistry.UpdateConnection(key, connection, PeerConnectionStatus.Connected);

            _logger.LogDebug("Peer {Peer} connected ({Count}/{Max} connections)",

                peerInfo.EndPoint, ConnectedPeerCount, _settings.MaxConnections);

            PeerConnected?.Invoke(this, new PeerConnectedEventArgs(connection));

            return true;

        }

        catch (Exception ex)

        {

            _logger.LogDebug(ex, "Failed to connect to peer {Peer}", peerInfo.EndPoint);

            // Record failure

            _peerRegistry.RecordConnectionFailure(key);

            _peerRegistry.UpdateConnection(key, null, PeerConnectionStatus.Disconnected);

            return false;

        }

    }

    /// <summary>

    /// Determines if a new peer should replace an existing one based on priority.

    /// Uses libtorrent-style priority comparison:

    /// 1. Lower fail count is better

    /// 2. Local peers are preferred

    /// 3. Global peer priority (CRC32 XOR of endpoints)

    /// </summary>

    private bool TryFindPeerToReplace(PeerInfo newPeer, out IPeerConnection peerToDisconnect)

    {

        peerToDisconnect = null;

        var connectedPeers = _peerRegistry.GetAllConnectedPeers();

        if (!connectedPeers.Any())

            return true;

        // Calculate new peer's priority

        uint newPeerPriority = _priorityCalculator.CalculatePriority(_localEndpoint, newPeer.EndPoint);

        bool newPeerIsLocal = IsLocalPeer(newPeer.EndPoint);

        int newPeerFailCount = _peerRegistry.GetFailCount(PeerRegistry.GetPeerKey(newPeer));

        // Find the worst (lowest priority) connected peer

        IPeerConnection worstPeer = null;

        uint worstPeerPriority = uint.MaxValue;

        int worstPeerScore = int.MaxValue;

        foreach (var peer in connectedPeers)

        {

            if (peer.PeerInfo?.EndPoint == null) continue;

            // Calculate composite score (lower is worse/more replaceable)

            int peerScore = CalculatePeerScore(peer);

            uint peerPriority = _priorityCalculator.CalculatePriority(_localEndpoint, peer.PeerInfo.EndPoint);

            // Find the peer with lowest score, or lowest priority if scores are equal

            if (peerScore < worstPeerScore ||

                (peerScore == worstPeerScore && peerPriority < worstPeerPriority))

            {

                worstPeer = peer;

                worstPeerPriority = peerPriority;

                worstPeerScore = peerScore;

            }

        }

        if (worstPeer == null)

            return false;

        // Calculate new peer's score

        int newPeerScore = 100; // Base score

        if (newPeerIsLocal) newPeerScore += 50; // Local peers get bonus

        newPeerScore -= newPeerFailCount * 10; // Penalty for failures

        // New peer should replace only if significantly better

        // (add hysteresis to prevent churn)

        const int ScoreHysteresis = 30;  // New peer must be 30+ points better

        if (newPeerScore > worstPeerScore + ScoreHysteresis)

        {

            peerToDisconnect = worstPeer;

            _logger.LogDebug("Peer replacement: new score {New} > worst score {Worst} + {Hysteresis}",

                newPeerScore, worstPeerScore, ScoreHysteresis);

            return true;

        }

        return false;

    }

    /// <summary>

    /// Calculates a score for a connected peer (higher is better, less likely to be replaced).

    /// </summary>

    private int CalculatePeerScore(IPeerConnection peer)

    {

        int score = 100; // Base score

        // Local peers are more valuable

        if (IsLocalPeer(peer.PeerInfo.EndPoint))

            score += 50;

        // Peers sending us data are valuable

        if (_statisticsTracker != null)

        {

            double downloadRate = _statisticsTracker.GetPeerDownloadRate(peer);

            if (downloadRate > 512 * 1024) // > 512 KB/s

                score += 100;

            else if (downloadRate > 64 * 1024) // > 64 KB/s

                score += 50;

            else if (downloadRate > 0)

                score += 25;

        }

        // Peers we've uploaded to recently are part of TFT

        if (peer.BytesUploaded > 0)

            score += 20;

        // Long-lived connections are valuable

        var connectionDuration = DateTime.UtcNow - peer.ConnectedAt;

        if (connectionDuration > TimeSpan.FromMinutes(5))

            score += 30;

        // Penalty for fail count from registry

        string key = PeerRegistry.GetPeerKey(peer.PeerInfo);

        int failCount = _peerRegistry.GetFailCount(key);

        score -= failCount * 10;

        return score;

    }

    /// <summary>

    /// Checks if an endpoint is on the local network.

    /// </summary>

    private static bool IsLocalPeer(IPEndPoint endpoint)

    {

        if (endpoint?.Address == null) return false;

        var addr = endpoint.Address;

        // Check for loopback

        if (IPAddress.IsLoopback(addr))

            return true;

        // IPv4 local ranges

        byte[] bytes = addr.GetAddressBytes();

        if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)

        {

            // 10.0.0.0/8

            if (bytes[0] == 10)

                return true;

            // 172.16.0.0/12

            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)

                return true;

            // 192.168.0.0/16

            if (bytes[0] == 192 && bytes[1] == 168)

                return true;

            // 169.254.0.0/16 (link-local)

            if (bytes[0] == 169 && bytes[1] == 254)

                return true;

        }

        // IPv6 link-local

        if (addr.IsIPv6LinkLocal)

            return true;

        return false;

    }

    /// <summary>
    /// Classifies a peer by IP address and returns a bandwidth limiter that enforces
    /// both the torrent-level limits and the peer-class limits.
    /// Returns the base limiter unchanged if peer classes are disabled or the peer is in the default class.
    /// </summary>
    private IPeerBandwidthLimiter? GetClassAwareLimiter(PeerInfo peerInfo)
    {
        if (_peerClassManager == null || peerInfo.IsI2p)
            return _bandwidthLimiter;

        var peerClass = _peerClassManager.Classify(peerInfo.IpAddress);
        if (peerClass.Id == 0) // Default class — no additional limiting
            return _bandwidthLimiter;

        return new PeerClassBandwidthLimiter(
            _bandwidthLimiter,
            peerClass.UploadChannel,
            peerClass.DownloadChannel);
    }

    /// <summary>
    /// Establishes the outbound transport through the session TransportConnector (uTP-first,
    /// TCP fallback), then layers MSE over the returned stream when policy/peer state calls for it.
    /// On MSE failure with a non-Forced policy, reconnects fresh for the plaintext attempt because
    /// the outbound DH key has already dirtied the first stream. Mirrors libtorrent's connect-then-
    /// encrypt ordering (bt_peer_connection::on_connected) and its reconnect-on-pe-failure fallback.
    /// </summary>
    internal async Task<(ITransportStream transport, bool isEncrypted, bool handshakeAlreadySent)>
        EstablishOutboundTransportAsync(PeerInfo peerInfo, byte[] peerId, CancellationToken cancellationToken)
    {
        var stream = await _transportConnector.ConnectAsync(peerInfo.NetworkEndPoint, cancellationToken)
            .ConfigureAwait(false);

        if (!ShouldAttemptMse(peerInfo, outbound: true))
            return (stream, isEncrypted: false, handshakeAlreadySent: false);

        try
        {
            var mseStream = await MseTransportStream.CreateOutboundAsync(
                stream, _infoHash, peerId, _encryptionMonitor, _mseLogger, cancellationToken)
                .ConfigureAwait(false);

            peerInfo.EncryptionSupport = MsePeerEncryptionSupport.Supported;
            return (mseStream, mseStream.IsEncrypted, mseStream.InitialPayloadSent);
        }
        catch (Exception ex) when (ex is MseNegotiationException or OperationCanceledException or IOException or SocketException)
        {
            _logger.LogDebug("MSE outbound failed for {Peer}: {Message}", peerInfo.EndPoint, ex.Message);
            peerInfo.EncryptionSupport = MsePeerEncryptionSupport.Unsupported;
            await stream.DisposeAsync().ConfigureAwait(false);

            if (_encryptionMonitor.CurrentValue.OutPolicy == EncryptionPolicy.Forced)
                throw;

            var plaintext = await _transportConnector.ConnectAsync(peerInfo.NetworkEndPoint, cancellationToken)
                .ConfigureAwait(false);
            return (plaintext, isEncrypted: false, handshakeAlreadySent: false);
        }
    }

    private async Task<IPeerConnection> CreateAndConnectPeerAsync(PeerInfo peerInfo, CancellationToken cancellationToken)

    {

        var peerLogger = _loggerFactory.CreateLogger<PeerConnection>();

        var peerId = Encoding.ASCII.GetBytes(_settings.PeerId);

        var (effectiveTransport, isEncrypted, handshakeAlreadySent) =
            await EstablishOutboundTransportAsync(peerInfo, peerId, cancellationToken).ConfigureAwait(false);

        try

        {

            // Classify peer and get class-aware bandwidth limiter
            var effectiveLimiter = GetClassAwareLimiter(peerInfo);

            var connection = new PeerConnection(

                peerInfo,

                _settings,

                effectiveTransport,

                peerLogger,

                onBytesDownloaded: _statisticsTracker != null

                    ? (peer, bytes) => _statisticsTracker.RecordDownload(peer, bytes)

                    : null,

                onBytesUploaded: _statisticsTracker != null

                    ? (peer, bytes) => _statisticsTracker.RecordUpload(peer, bytes)

                    : null,

                onPayloadDownloaded: null,

                onPayloadUploaded: _statisticsTracker != null

                    ? (peer, bytes) => _statisticsTracker.RecordPayloadUpload(peer, bytes)

                    : null,

                loggerFactory: _loggerFactory,

                bandwidthLimiter: effectiveLimiter,

                externalIpVoter: _externalIpVoter,

                privacyMonitor: _privacyMonitor);

            connection.IsEncrypted = isEncrypted;

            connection.HandshakeAlreadySent = handshakeAlreadySent;

            if (_hashExchangeHandler != null)

                connection.HashExchangeHandler = _hashExchangeHandler;

            _peerExtensionSetup?.Invoke(connection);

            // Wire up events

            connection.MessageReceived += OnPeerMessageReceived;

            connection.ConnectionLost += (s, e) => _ = HandleConnectionLostAsync(connection, e.Reason);

            // Connect (handshake)

            await connection.ConnectAsync(_infoHash, cancellationToken).ConfigureAwait(false);

            // Send our bitfield immediately after handshake (BitTorrent protocol requirement)

            if (_localBitfieldProvider != null)

            {

                try

                {

                    var bitfield = _localBitfieldProvider();

                    if (bitfield != null && bitfield.Length > 0)

                    {

                        await connection.SendBitfieldAsync(bitfield, cancellationToken).ConfigureAwait(false);

                    }

                }

                catch (Exception ex)

                {

                    _logger.LogDebug(ex, "Failed to send bitfield to {Peer}", peerInfo.EndPoint);

                }

            }

            return connection;

        }

        catch

        {

            await effectiveTransport.DisposeAsync().ConfigureAwait(false);

            throw;

        }

    }

    public async Task AddPeersAsync(IEnumerable<PeerInfo> peers, CancellationToken cancellationToken = default)

    {

        if (peers == null)

            throw new ArgumentNullException(nameof(peers));

        // Sort peers by priority before adding (highest priority first)

        var sortedPeers = peers

            .OrderByDescending(p => IsLocalPeer(p.EndPoint) ? 1 : 0) // Local peers first

            .ThenBy(p => _peerRegistry.GetFailCount(PeerRegistry.GetPeerKey(p))) // Lower fail count

            .ThenByDescending(p => _priorityCalculator.CalculatePriority(_localEndpoint, p.EndPoint));

        var tasks = sortedPeers.Select(peer => AddPeerAsync(peer, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);

    }

    /// <summary>

    /// Connect boost: immediately attempt connections to discovered/disconnected peers.

    /// Follows libtorrent's do_connect_boost() pattern for fast peer acquisition.

    /// Called after tracker/DHT returns peers to avoid waiting for normal connection cycle.

    /// </summary>

    public void TriggerConnectionAttempts()

    {

        if (!_isRunning)

            return;

        // Skip outgoing connections if seeding and SeedingOutgoingConnections is disabled

        if (_isSeeding && !_seedingOutgoingConnections)

            return;

        // Get peers that need connection attempts

        var discoveredPeers = _peerRegistry.GetAllByStatus(PeerConnectionStatus.Discovered);

        var disconnectedPeers = _peerRegistry.GetAllByStatus(PeerConnectionStatus.Disconnected);

        var candidatePeers = discoveredPeers

            .Concat(disconnectedPeers)

            .Where(p => !_peerRegistry.IsBanned(PeerRegistry.GetPeerKey(p.Info)))

            .OrderByDescending(p => p.Score?.Priority ?? 0) // Highest scoring peers first

            .Take(_settings.MaxConnections - ConnectedPeerCount) // Only fill up to max

            .ToList();

        if (candidatePeers.Count == 0)

            return;

        if (Interlocked.CompareExchange(ref _connectBoostRunning, 1, 0) != 0)

            return; // a previous drain loop is still running

        _logger.LogDebug("Connect boost: attempting {Count} peer connections", candidatePeers.Count);

        // Captured as a value before scheduling — safe even if the CTS is disposed later.

        var stopToken = _stopCts.Token;

        // Fire-and-forget connection attempts with rate limiting

        _ = Task.Run(async () =>

        {

            try

            {

                foreach (var peer in candidatePeers)

                {

                    if (!_isRunning || ConnectedPeerCount >= _settings.MaxConnections)

                        break;

                    // Rate-limit connection attempts per libtorrent connection_speed.
                    // NOTE: libtorrent enforces this session-wide in session_impl::on_tick().
                    // vTorrent enforces per-PeerManager (per-torrent). With N active torrents,
                    // total session rate = N * ConnectionSpeed. Acceptable for now.
                    var now = DateTime.UtcNow;

                    if ((now - _lastTickReset).TotalSeconds >= 1.0)

                    {

                        _connectionsThisTick = 0;

                        _lastTickReset = now;

                    }

                    var maxSpeed = _connectionMonitor?.CurrentValue.ConnectionSpeed ?? 30;

                    if (_connectionsThisTick >= maxSpeed)

                    {

                        // Rate limit reached — wait out the rest of this 1s tick instead of

                        // abandoning the remaining candidates. Resume reconnect drains the

                        // whole preserved swarm through this loop; the old 'break' silently

                        // dropped everything past the first ~ConnectionSpeed peers.

                        var remaining = TimeSpan.FromSeconds(1.0) - (DateTime.UtcNow - _lastTickReset);

                        if (remaining > TimeSpan.Zero)

                            await Task.Delay(remaining, stopToken).ConfigureAwait(false);

                        if (!_isRunning || ConnectedPeerCount >= _settings.MaxConnections)

                            break;

                        _connectionsThisTick = 0;

                        _lastTickReset = DateTime.UtcNow;

                    }

                    _connectionsThisTick++;

                    try

                    {

                        await AddPeerAsync(peer.Info, CancellationToken.None).ConfigureAwait(false);

                    }

                    catch (Exception ex)

                    {

                        _logger.LogDebug(ex, "Connect boost: failed to connect to {Peer}", peer.Info.EndPoint);

                    }

                }

            }

            finally

            {

                Interlocked.Exchange(ref _connectBoostRunning, 0);

            }

        });

    }

    public async Task RemovePeerAsync(IPeerConnection peer)

    {

        if (peer == null)

            return;

        string key = PeerRegistry.GetPeerKey(peer.PeerInfo);

        if (_peerRegistry.TryGetConnected(key, out var removedPeer))

        {

            _logger.LogDebug("Removing peer {Peer}", peer.PeerInfo.EndPoint);

            await DisconnectPeerAsync(removedPeer).ConfigureAwait(false);

            _peerRegistry.UpdateConnection(key, null, PeerConnectionStatus.Disconnected);

            PeerDisconnected?.Invoke(this, new PeerDisconnectedEventArgs(peer.PeerInfo, "Removed by manager"));

        }

    }

    public async Task BroadcastHaveAsync(int pieceIndex, CancellationToken cancellationToken = default)

    {

        // Super-seeding: suppress HAVE broadcasts — pieces revealed individually
        if (SuperSeedingActive)
            return;

        var connectedPeers = _peerRegistry.GetAllConnectedPeers();

        _logger.LogDebug("Broadcasting Have({PieceIndex}) to {Count} peers", pieceIndex, connectedPeers.Count);

        var tasks = connectedPeers.Select(peer => SafeExecuteAsync(() => peer.AnnounceHaveAsync(pieceIndex, cancellationToken)));

        await Task.WhenAll(tasks).ConfigureAwait(false);

    }

    public async Task BroadcastBitfieldAsync(byte[] bitfield, CancellationToken cancellationToken = default)

    {

        var connectedPeers = _peerRegistry.GetAllConnectedPeers();

        _logger.LogDebug("Broadcasting bitfield to {Count} peers", connectedPeers.Count);

        var tasks = connectedPeers.Select(peer =>

            SafeExecuteAsync(() => peer.SendBitfieldAsync(bitfield, cancellationToken)));

        await Task.WhenAll(tasks).ConfigureAwait(false);

    }

    public IEnumerable<IPeerConnection> GetPeersWithPiece(int pieceIndex)

    {

        return _peerRegistry.GetAllConnectedPeers().Where(peer =>

            {

                if (peer.PeerBitfield == null)

                    return false;

                int byteIndex = pieceIndex / 8;

                int bitIndex = 7 - (pieceIndex % 8);

                if (byteIndex >= peer.PeerBitfield.Length)

                    return false;

                return (peer.PeerBitfield[byteIndex] & (1 << bitIndex)) != 0;

            }

        );

    }

    public IEnumerable<IPeerConnection> GetAvailablePeers()

    {

        return _peerRegistry.GetAllConnectedPeers().Where(peer =>

            peer.IsConnected && !peer.IsChoked && peer.IsInterested);

    }

    public IEnumerable<IPeerConnection> GetInterestedPeers()

    {

        return _peerRegistry.GetAllConnectedPeers().Where(peer =>

            peer.IsConnected && peer.PeerIsInterested && !peer.IsChoking);

    }

    /// <summary>

    /// Gets peers sorted by download rate (highest first).

    /// Useful for piece affinity and choking decisions.

    /// </summary>

    public IEnumerable<IPeerConnection> GetPeersByDownloadRate()

    {

        if (_statisticsTracker == null)

            return _peerRegistry.GetAllConnectedPeers();

        return _peerRegistry.GetAllConnectedPeers()

            .OrderByDescending(p => _statisticsTracker.GetPeerDownloadRate(p));

    }

    private void OnPeerMessageReceived(object sender, PeerMessageReceivedEventArgs e)

    {

        if (sender is IPeerConnection peer)

        {

            MessageReceived?.Invoke(this, new PeerMessageEventArgs(peer, e.Message));

        }

    }

    private async Task HandleConnectionLostAsync(IPeerConnection peer, string reason)

    {

        string key = PeerRegistry.GetPeerKey(peer.PeerInfo);

        if (_peerRegistry.TryGetConnected(key, out var removedPeer))

        {

            _logger.LogDebug("Peer {Peer} disconnected: {Reason}", peer.PeerInfo.EndPoint, reason);

            await DisconnectPeerAsync(removedPeer).ConfigureAwait(false);

            _peerRegistry.UpdateConnection(key, null, PeerConnectionStatus.Disconnected);

            PeerDisconnected?.Invoke(this, new PeerDisconnectedEventArgs(peer.PeerInfo, reason));

        }

    }

    private async Task DisconnectPeerAsync(IPeerConnection peer)

    {

        try

        {

            await peer.DisconnectAsync().ConfigureAwait(false);

        }

        catch (Exception ex)

        {

            _logger.LogDebug(ex, "Error disconnecting peer {Peer}", peer.PeerInfo.EndPoint);

        }

        finally

        {

            peer.Dispose();

        }

    }

    private async Task SafeExecuteAsync(Func<Task> action)

    {

        try

        {

            await action().ConfigureAwait(false);

        }

        catch (Exception ex)

        {

            _logger.LogDebug(ex, "Error executing peer action");

        }

    }

    public IPeerConnection GetPeer(PeerInfo peerInfo)

    {

        string key = PeerRegistry.GetPeerKey(peerInfo);

        _peerRegistry.TryGetConnected(key, out var peer);

        return peer;

    }

    public bool IsConnected(PeerInfo peerInfo)

    {

        string key = PeerRegistry.GetPeerKey(peerInfo);

        return _peerRegistry.TryGetConnected(key, out _);

    }

    public void Dispose()

    {

        if (!_stopCts.IsCancellationRequested)

        {

            _stopCts.Cancel();

        }

        // Non-blocking: force-close all peers without waiting for graceful disconnect

        var connectedPeers = _peerRegistry.GetAllConnectedPeers();

        foreach (var peer in connectedPeers)

        {

            try

            {

                peer.Dispose();

            }

            catch (Exception ex)

            {

                _logger?.LogDebug(ex, "Error disposing peer during PeerManager.Dispose");

            }

        }

        _peerRegistry.Clear();

        _connectTopUpTimer?.Dispose();

        _stopCts?.Dispose();

    }

}
