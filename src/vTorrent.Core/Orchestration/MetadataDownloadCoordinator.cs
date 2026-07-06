using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.PeerCommunication.Events;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Extensions;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Transport;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Models;
using vTorrent.Core.Utilities;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Coordinates metadata download for magnet links.
///
/// Based on libtorrent's ut_metadata implementation:
/// - Manages peer connections specifically for metadata retrieval
/// - Registers UtMetadataExtension with each peer
/// - Coordinates metadata piece requests across multiple peers
/// - Validates completed metadata against info hash
/// - Notifies when metadata is successfully downloaded
///
/// Reference: libtorrent/src/ut_metadata.cpp, libtorrent/src/torrent.cpp
/// </summary>
public class MetadataDownloadCoordinator : IDisposable
{
    private readonly ILogger<MetadataDownloadCoordinator> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly byte[] _infoHash;
    private readonly string _infoHashHex;
    private readonly PeerSettings _peerSettings;
    private readonly IOptionsMonitor<PeerSettings> _peerMonitor;
    private readonly ITransportConnector _transportConnector;
    private readonly ManagedTorrent _torrent;

    // Peer management
    private readonly ConcurrentDictionary<string, PeerConnectionState> _peers = new();
    private readonly ConcurrentQueue<PeerInfo> _pendingPeers = new();
    private readonly object _connectionLock = new();

    // Metadata assembly
    private readonly object _metadataLock = new();
    private byte[] _metadataBuffer;
    private int _metadataSize;
    private int _totalPieces;
    private readonly HashSet<int> _receivedPieces = new();
    private readonly ConcurrentDictionary<int, (DateTime RequestTime, string PeerId)> _pendingRequests = new();

    // Configuration (based on libtorrent defaults)
    public const int MetadataPieceSize = 16384; // 16 KB per BEP 9
    public const int MaxConcurrentPeers = 10;
    public const int MaxConcurrentRequestsPerPeer = 2;
    public const int MaxTotalPendingRequests = 20;
    public readonly TimeSpan RequestTimeout;
    public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PeerPenaltyDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    // State
    private bool _isRunning;
    private bool _isComplete;
    private bool _isDisposed;
    private CancellationTokenSource _cts;
    private Task _tickTask;

    /// <summary>
    /// Raised when metadata download progresses.
    /// </summary>
    public event Action<int, int> ProgressChanged;

    /// <summary>
    /// Raised when metadata is successfully downloaded and validated.
    /// </summary>
    public event Action<byte[]> MetadataReceived;

    /// <summary>
    /// Raised when metadata download fails permanently.
    /// </summary>
    public event Action<string> MetadataFailed;

    /// <summary>
    /// Whether metadata download is complete.
    /// </summary>
    public bool IsComplete => _isComplete;

    /// <summary>
    /// Current progress (0.0 to 1.0).
    /// </summary>
    public double Progress
    {
        get
        {
            lock (_metadataLock)
            {
                if (_totalPieces == 0) return 0;
                return (double)_receivedPieces.Count / _totalPieces;
            }
        }
    }

    /// <summary>
    /// Number of connected peers.
    /// </summary>
    public int ConnectedPeerCount => _peers.Count(p => p.Value.IsConnected);

    /// <summary>
    /// Number of peers with metadata.
    /// </summary>
    public int PeersWithMetadata => _peers.Count(p => p.Value.HasMetadata);

    public MetadataDownloadCoordinator(
        ILogger<MetadataDownloadCoordinator> logger,
        ILoggerFactory loggerFactory,
        byte[] infoHash,
        PeerSettings peerSettings,
        ITransportConnector transportConnector,
        ManagedTorrent torrent,
        int requestTimeoutSeconds = 30,
        IOptionsMonitor<PeerSettings> peerMonitor = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _infoHash = infoHash ?? throw new ArgumentNullException(nameof(infoHash));
        _peerSettings = peerSettings ?? throw new ArgumentNullException(nameof(peerSettings));
        _peerMonitor = peerMonitor;
        _transportConnector = transportConnector ?? throw new ArgumentNullException(nameof(transportConnector));
        _torrent = torrent ?? throw new ArgumentNullException(nameof(torrent));
        RequestTimeout = TimeSpan.FromSeconds(requestTimeoutSeconds);

        if (_infoHash.Length != 20)
            throw new ArgumentException("Info hash must be 20 bytes");

        _infoHashHex = Convert.ToHexString(_infoHash);
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Starts the metadata download coordinator.
    /// </summary>
    public void Start()
    {
        if (_isRunning || _isComplete || _isDisposed)
            return;

        _isRunning = true;
        _logger.LogDebug("Starting metadata download for {InfoHash}", _infoHashHex);

        // Start the tick loop
        _tickTask = Task.Run(TickLoopAsync);
    }

    /// <summary>
    /// Stops the metadata download coordinator.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _cts.Cancel();

        // Wait for tick task to complete
        if (_tickTask != null)
        {
            try
            {
                await _tickTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Tick task did not stop gracefully");
            }
        }

        // Disconnect all peers
        foreach (var (key, state) in _peers)
        {
            await DisconnectPeerAsync(state);
        }

        _peers.Clear();
        _logger.LogDebug("Metadata download coordinator stopped for {InfoHash}", _infoHashHex);
    }

    /// <summary>
    /// Adds a peer to try for metadata download.
    /// </summary>
    public void AddPeer(PeerInfo peerInfo)
    {
        if (peerInfo == null || _isComplete || !_isRunning)
            return;

        _pendingPeers.Enqueue(peerInfo);
        _logger.LogDebug("Added peer candidate: {Peer}", peerInfo.EndPoint);
    }

    /// <summary>
    /// Adds multiple peers to try for metadata download.
    /// </summary>
    public void AddPeers(IEnumerable<PeerInfo> peers)
    {
        if (peers == null || _isComplete || !_isRunning)
            return;

        foreach (var peer in peers)
        {
            _pendingPeers.Enqueue(peer);
        }
    }

    /// <summary>
    /// Main tick loop - manages connections and requests.
    /// </summary>
    private async Task TickLoopAsync()
    {
        while (_isRunning && !_isComplete && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Connect to new peers if needed
                await TryConnectNewPeersAsync();

                // Clean up timed out requests
                CleanupTimedOutRequests();

                // Send metadata requests
                await SendMetadataRequestsAsync();

                // Update torrent progress
                UpdateTorrentProgress();

                await Task.Delay(TickInterval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in metadata tick loop");
            }
        }
    }

    /// <summary>
    /// Tries to connect to new peers from the pending queue.
    /// </summary>
    private async Task TryConnectNewPeersAsync()
    {
        while (ConnectedPeerCount < MaxConcurrentPeers && _pendingPeers.TryDequeue(out var peerInfo))
        {
            if (_isComplete || !_isRunning)
                break;

            var key = GetPeerKey(peerInfo.EndPoint);
            if (_peers.ContainsKey(key))
                continue;

            try
            {
                await ConnectToPeerAsync(peerInfo);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to connect to peer {Peer}", peerInfo.EndPoint);
            }
        }
    }

    /// <summary>
    /// Connects to a peer and sets up the metadata extension.
    /// </summary>
    private async Task ConnectToPeerAsync(PeerInfo peerInfo)
    {
        var key = GetPeerKey(peerInfo.EndPoint);
        var peerLogger = _loggerFactory.CreateLogger<PeerConnection>();

        var transport = await _transportConnector.ConnectAsync(
            peerInfo.EndPoint, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);

        var connection = new PeerConnection(
            peerInfo,
            _peerSettings,
            transport,
            peerLogger,
            loggerFactory: _loggerFactory);

        var state = new PeerConnectionState
        {
            Connection = connection,
            PeerInfo = peerInfo,
            Key = key
        };

        // Create and register the ut_metadata extension
        var extLogger = _loggerFactory.CreateLogger<UtMetadataExtension>();
        var extension = new UtMetadataExtension(
            extLogger,
            _infoHash,
            getMetadata: null, // We don't have metadata yet
            onMetadataReceived: data => OnMetadataPieceReceived(state, data),
            isEnabled: true);

        extension.ProgressChanged += (received, total) =>
        {
            _logger.LogDebug("Peer {Peer} metadata progress: {Received}/{Total}", key, received, total);
        };

        state.MetadataExtension = extension;
        connection.RegisterExtension(extension);

        // Wire up events
        connection.MessageReceived += (s, e) => OnPeerMessageReceived(state, e);
        connection.ConnectionLost += (s, e) => OnPeerConnectionLost(state, e);

        // Add to peers before connecting
        if (!_peers.TryAdd(key, state))
        {
            _logger.LogDebug("Peer {Peer} already exists", key);
            return;
        }

        try
        {
            // Connect with timeout
            using var connectCts = new CancellationTokenSource(ConnectionTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(connectCts.Token, _cts.Token);

            await connection.ConnectAsync(_infoHash, linkedCts.Token);
            state.IsConnected = true;
            state.ConnectedAt = DateTime.UtcNow;

            _logger.LogDebug("Connected to peer {Peer} for metadata download", peerInfo.EndPoint);

            // Check if peer has metadata after extension handshake
            // The extension will have received metadata_size if peer supports ut_metadata
            await Task.Delay(500, _cts.Token); // Give time for extension handshake

            if (extension.PeerHasMetadata)
            {
                state.HasMetadata = true;
                _logger.LogDebug("Peer {Peer} has metadata", key);
            }
        }
        catch (OperationCanceledException)
        {
            _peers.TryRemove(key, out _);
            await DisconnectPeerAsync(state);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to connect to peer {Peer}", peerInfo.EndPoint);
            _peers.TryRemove(key, out _);
            state.FailCount++;
        }
    }

    /// <summary>
    /// Sends metadata requests to connected peers.
    /// </summary>
    private async Task SendMetadataRequestsAsync()
    {
        foreach (var (key, state) in _peers)
        {
            if (!state.IsConnected || !state.HasMetadata || _isComplete)
                continue;

            // Check if this peer is penalized
            if (state.PenalizedUntil > DateTime.UtcNow)
                continue;

            // Check peer's request limit
            var peerPendingCount = _pendingRequests.Count(kvp => kvp.Value.PeerId == key);
            if (peerPendingCount >= MaxConcurrentRequestsPerPeer)
                continue;

            // Check global pending limit
            if (_pendingRequests.Count >= MaxTotalPendingRequests)
                continue;

            // Find next piece to request
            int? pieceToRequest = null;
            lock (_metadataLock)
            {
                for (int i = 0; i < _totalPieces; i++)
                {
                    if (_receivedPieces.Contains(i))
                        continue;
                    if (_pendingRequests.ContainsKey(i))
                        continue;

                    pieceToRequest = i;
                    break;
                }
            }

            if (pieceToRequest == null)
                continue;

            // Record the request
            _pendingRequests[pieceToRequest.Value] = (DateTime.UtcNow, key);

            _logger.LogDebug("Requesting metadata piece {Piece} from {Peer}", pieceToRequest.Value, key);

            // The extension's GenerateMessageAsync will be called by the tick timer
            // We need to trigger it explicitly
            try
            {
                var messageData = await state.MetadataExtension.GenerateMessageAsync(_cts.Token);
                if (messageData != null && state.Connection.ExtensionManager != null)
                {
                    var remoteId = state.MetadataExtension.RemoteExtensionId;
                    if (remoteId.HasValue)
                    {
                        var message = PeerMessage.CreateExtended(remoteId.Value, messageData);
                        await state.Connection.SendMessageAsync(message, _cts.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send metadata request to {Peer}", key);
                _pendingRequests.TryRemove(pieceToRequest.Value, out _);
            }
        }
    }

    /// <summary>
    /// Called when a metadata piece is received from a peer.
    /// </summary>
    private void OnMetadataPieceReceived(PeerConnectionState state, byte[] metadata)
    {
        // This is called when the extension has assembled complete metadata
        // Validate against info hash
        var hash = SHA1.HashData(metadata);

        if (!hash.SequenceEqual(_infoHash))
        {
            _logger.LogWarning("Metadata validation failed from {Peer}: hash mismatch", state.Key);

            // Penalize this peer
            state.PenalizedUntil = DateTime.UtcNow + TimeSpan.FromMinutes(5);
            state.FailCount++;

            // Reset the extension to try again
            state.MetadataExtension.Reset();
            return;
        }

        _logger.LogDebug("Metadata successfully received and validated ({Size} bytes)", metadata.Length);
        _isComplete = true;

        // Update torrent
        _torrent.MetadataPiecesReceived = _totalPieces;
        _torrent.MetadataProgress = 1.0;

        // Notify listeners
        MetadataReceived?.Invoke(metadata);
    }

    /// <summary>
    /// Called when metadata size is learned from a peer.
    /// </summary>
    public void SetMetadataSize(int size)
    {
        lock (_metadataLock)
        {
            if (_metadataSize > 0)
                return; // Already set

            if (size <= 0 || size > (_peerMonitor?.CurrentValue.MaxMetadataSize ?? 31457280))
            {
                _logger.LogWarning("Invalid metadata size: {Size}", size);
                return;
            }

            _metadataSize = size;
            _totalPieces = (size + MetadataPieceSize - 1) / MetadataPieceSize;
            _metadataBuffer = new byte[size];

            _logger.LogDebug("Metadata size set: {Size} bytes ({Pieces} pieces)", size, _totalPieces);

            // Update torrent
            _torrent.MetadataPiecesTotal = _totalPieces;
        }
    }

    /// <summary>
    /// Records a received metadata piece.
    /// </summary>
    public void RecordPieceReceived(int piece, string peerId)
    {
        lock (_metadataLock)
        {
            if (_receivedPieces.Add(piece))
            {
                _pendingRequests.TryRemove(piece, out _);
                ProgressChanged?.Invoke(_receivedPieces.Count, _totalPieces);

                _logger.LogDebug("Received metadata piece {Piece}/{Total}", piece + 1, _totalPieces);
            }
        }
    }

    /// <summary>
    /// Cleans up timed out requests.
    /// </summary>
    private void CleanupTimedOutRequests()
    {
        var now = DateTime.UtcNow;
        var timedOut = _pendingRequests
            .Where(kvp => now - kvp.Value.RequestTime > RequestTimeout)
            .ToList();

        foreach (var (piece, (_, peerId)) in timedOut)
        {
            _pendingRequests.TryRemove(piece, out _);
            _logger.LogDebug("Metadata request for piece {Piece} timed out (peer: {Peer})", piece, peerId);
        }
    }

    /// <summary>
    /// Updates the torrent's metadata progress.
    /// </summary>
    private void UpdateTorrentProgress()
    {
        lock (_metadataLock)
        {
            if (_totalPieces > 0)
            {
                _torrent.MetadataPiecesReceived = _receivedPieces.Count;
                _torrent.MetadataProgress = (double)_receivedPieces.Count / _totalPieces;
            }
        }
    }

    private void OnPeerMessageReceived(PeerConnectionState state, PeerMessageReceivedEventArgs e)
    {
        // Extended messages are handled by the extension manager
        // We might want to handle other messages here
    }

    private void OnPeerConnectionLost(PeerConnectionState state, PeerConnectionLostEventArgs e)
    {
        _logger.LogDebug("Lost connection to peer {Peer}: {Reason}", state.Key, e.Reason);

        state.IsConnected = false;
        _peers.TryRemove(state.Key, out _);

        // Cancel pending requests from this peer
        var toRemove = _pendingRequests
            .Where(kvp => kvp.Value.PeerId == state.Key)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var piece in toRemove)
        {
            _pendingRequests.TryRemove(piece, out _);
        }
    }

    private async Task DisconnectPeerAsync(PeerConnectionState state)
    {
        if (state?.Connection == null)
            return;

        try
        {
            await state.Connection.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disconnecting peer {Peer}", state.Key);
        }
    }

    private static string GetPeerKey(IPEndPoint endpoint)
    {
        return $"{endpoint.Address}:{endpoint.Port}";
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _isRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();

        foreach (var (_, state) in _peers)
        {
            state.Connection?.DisconnectAsync().FireAndForget(_logger);
        }

        _peers.Clear();
    }

    /// <summary>
    /// Tracks state for each peer connection.
    /// </summary>
    private class PeerConnectionState
    {
        public PeerConnection Connection { get; set; }
        public PeerInfo PeerInfo { get; set; }
        public UtMetadataExtension MetadataExtension { get; set; }
        public string Key { get; set; }
        public bool IsConnected { get; set; }
        public bool HasMetadata { get; set; }
        public DateTime ConnectedAt { get; set; }
        public DateTime PenalizedUntil { get; set; }
        public int FailCount { get; set; }
    }
}
