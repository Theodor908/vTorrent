using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Models;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Engine;
using vTorrent.Core.PeerCommunication;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Utilities;
using vTorrent.Core.PieceIO;

namespace vTorrent.Core.Download;

/// <summary>
/// Dispatches block requests to peers with slow-start window management,
/// timeout handling, and snub detection. Extracted from DownloadCoordinator (Phase 4 refactor).
/// </summary>
internal sealed partial class RequestDispatcher
{
    private readonly ILogger _logger;
    private readonly IStatisticsTracker _statisticsTracker;
    private readonly TorrentInfo _torrentInfo;
    private readonly PeerSettings _settings;
    private readonly int _blockSize;
    private readonly IOptionsMonitor<PeerSettings>? _peerMonitor;

    // Block request tracking
    private readonly ConcurrentDictionary<BlockRequest, PendingBlock> _pendingBlocks = new();
    private readonly ConcurrentDictionary<IPeerConnection, StrongBox<int>> _pendingCountByPeer = new();
    private readonly ConcurrentDictionary<string, byte[]> _peerBitfieldCache = new();
    private readonly ConcurrentDictionary<IPeerConnection, Bitfield> _cachedPeerBitfields = new();

    // Slow-start: per-peer window starts at 4, increments by 1 per received piece (like libtorrent)
    private readonly ConcurrentDictionary<IPeerConnection, int> _slowStartWindow = new();
    private const int InitialSlowStartWindow = 4;  // libtorrent default

    // Slow-start exit: track whether each peer has exited slow-start
    private readonly ConcurrentDictionary<IPeerConnection, bool> _slowStartExited = new();
    private readonly ConcurrentDictionary<IPeerConnection, double> _lastPeerRate = new();
    private const double SlowStartExitThresholdBytesPerSec = 10 * 1024; // 10 KB/s (libtorrent default)

    // Per-peer adaptive timeout tracking (EWMA of block delivery times)
    private readonly ConcurrentDictionary<IPeerConnection, PeerBlockTiming> _peerBlockTiming = new();

    // BEP 52: hash picker for v2/hybrid torrents (null for v1-only)
    private HashPicker? _hashPicker;

    // BEP 52: peer prober reference for hash exchange coordination
    private Interfaces.IPeerProber? _peerProber;

    public RequestDispatcher(
        TorrentInfo torrentInfo,
        PeerSettings settings,
        IStatisticsTracker statisticsTracker,
        ILogger logger,
        IOptionsMonitor<PeerSettings>? peerMonitor = null)
    {
        _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(torrentInfo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _statisticsTracker = statisticsTracker ?? throw new ArgumentNullException(nameof(statisticsTracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _blockSize = PeerConstants.BlockSize;
        _peerMonitor = peerMonitor;
    }

    // --- Properties ---

    public ConcurrentDictionary<BlockRequest, PendingBlock> PendingBlocks => _pendingBlocks;
    public ConcurrentDictionary<IPeerConnection, StrongBox<int>> PendingCountByPeer => _pendingCountByPeer;
    public ConcurrentDictionary<string, byte[]> PeerBitfieldCache => _peerBitfieldCache;
    public ConcurrentDictionary<IPeerConnection, int> SlowStartWindow => _slowStartWindow;
    public ConcurrentDictionary<IPeerConnection, bool> SlowStartExited => _slowStartExited;
    public ConcurrentDictionary<IPeerConnection, double> LastPeerRate => _lastPeerRate;
    public int PendingRequestCount => _pendingBlocks.Count;

    public Interfaces.IPeerProber? PeerProber { set => _peerProber = value; }
    public HashPicker? HashPickerInstance { set => _hashPicker = value; }

    // --- Request Dispatch ---

    /// <summary>
    /// Request blocks from a peer and return the count of requests sent.
    /// </summary>
    public async Task<int> RequestBlocksFromPeerWithCountAsync(
        IPeerConnection peer,
        PieceSelectionCoordinator pieceSelection,
        ConcurrentDictionary<int, PieceBlockTracker> inProgressPieces,
        CancellationToken cancellationToken,
        IReadOnlyList<KeyValuePair<int, PieceBlockTracker>>? inProgressSnapshot = null)
    {
        int currentPending = _pendingCountByPeer.TryGetValue(peer, out var pb) ? Volatile.Read(ref pb.Value) : 0;
        var maxPending = CalculateOptimalPipelineDepth(peer);
        int slotsAvailable = maxPending - currentPending;

        if (slotsAvailable <= 0)
            return 0;

        // BEP 52 hash gate: request hashes in parallel with blocks (don't stall the pipeline)
        if (_hashPicker is not null && peer.PeerBitfield is not null)
        {
            var peerBf = _cachedPeerBitfields.GetOrAdd(peer,
                p => new Bitfield(p.PeerBitfield, _torrentInfo.PieceCount));
            var hashReq = _hashPicker.PickHashRequest(peerBf);

            if (hashReq is not null)
            {
                await peer.SendHashRequestAsync(hashReq.Value, cancellationToken).ConfigureAwait(false);
            }
        }

        // Single lock acquisition for all blocks.
        // Pass peer download rate so SelectBlockBatch can apply WholePiecesThreshold.
        double peerRate = _statisticsTracker.GetPeerDownloadRate(peer);
        var blockRequests = pieceSelection.SelectBlockBatch(peer, slotsAvailable, inProgressPieces, _pendingBlocks, inProgressSnapshot, peerRate);

        if (blockRequests.Count == 0)
            return 0;

        var blocksToRequest = new List<(int pieceIndex, int begin, int length)>(blockRequests.Count);
        foreach (var b in blockRequests)
            blocksToRequest.Add((b.PieceIndex, b.Begin, b.Length));

        try
        {
            await peer.RequestBlocksBatchAsync(blocksToRequest, cancellationToken).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            int trackedCount = 0;

            foreach (var block in blockRequests)
            {
                // TryAdd: don't overwrite existing entries from other peers.
                // Endgame duplicates (already pending from another peer) are sent
                // but not tracked — they're "free" requests that don't affect
                // pipeline accounting, preventing _pendingCountByPeer leaks.
                if (_pendingBlocks.TryAdd(block, new PendingBlock
                {
                    Peer = peer,
                    PieceIndex = block.PieceIndex,
                    Begin = block.Begin,
                    Length = block.Length,
                    RequestedAt = now
                }))
                {
                    trackedCount++;
                }
            }

            if (trackedCount > 0)
            {
                var incBox = _pendingCountByPeer.GetOrAdd(peer, _ => new StrongBox<int>(0));
                Interlocked.Add(ref incBox.Value, trackedCount);
            }

            LogRequestedBlocks(_logger, blockRequests.Count, peer.PeerInfo.EndPoint,
                currentPending + trackedCount, maxPending, trackedCount);

            return blockRequests.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to request blocks from {Peer}", peer.PeerInfo.EndPoint);

            // Release blocks so they can be re-requested from another peer.
            foreach (var block in blockRequests)
            {
                if (inProgressPieces.TryGetValue(block.PieceIndex, out var progress))
                {
                    progress.MarkBlockNotRequested(block.Begin);
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// Calculates optimal request pipeline depth based on bandwidth-delay product (BDP).
    /// Mirrors libtorrent's update_desired_queue_size() in peer_connection.cpp.
    /// </summary>
    public int CalculateOptimalPipelineDepth(IPeerConnection peer)
    {
        var downloadRate = _statisticsTracker.GetPeerDownloadRate(peer);
        var hasExitedSlowStart = _slowStartExited.GetValueOrDefault(peer, false);
        var slowStartLimit = _slowStartWindow.GetValueOrDefault(peer, InitialSlowStartWindow);

        if (downloadRate <= 0)
            return hasExitedSlowStart ? InitialSlowStartWindow : slowStartLimit;

        double queueTimeSeconds;
        var rttMs = peer.RoundTripTimeMs;
        var requestQueueTime = _peerMonitor?.CurrentValue.RequestQueueTime ?? 3;

        if (rttMs > 0 && rttMs < 10000)
        {
            queueTimeSeconds = (rttMs / 1000.0) * 2.0;
            queueTimeSeconds = Math.Max(queueTimeSeconds, requestQueueTime);
        }
        else
        {
            queueTimeSeconds = requestQueueTime;
        }

        var bdpBlocks = (int)(queueTimeSeconds * downloadRate / _blockSize);
        var hardCap = _settings.MaxPendingBlocksPerPeer;
        var peerReqq = peer.RemoteRequestQueueSize;

        if (peerReqq.HasValue && peerReqq.Value > 0)
            hardCap = Math.Min(hardCap, peerReqq.Value);

        var optimal = Math.Clamp(bdpBlocks, 2, hardCap);

        // Web seeds: cap pipeline to concurrent HTTP slots × blocks per piece.
        if (peer is WebSeedConnection ws)
        {
            int blocksPerPiece = (int)Math.Ceiling((double)_torrentInfo.PieceLength / _blockSize);
            int webSeedCap = ws.MaxConcurrentRequests * blocksPerPiece;
            optimal = Math.Min(optimal, webSeedCap);
        }
        else if (peer is HttpSeedConnection hs)
        {
            int blocksPerPiece = (int)Math.Ceiling((double)_torrentInfo.PieceLength / _blockSize);
            int webSeedCap = hs.MaxConcurrentRequests * blocksPerPiece;
            optimal = Math.Min(optimal, webSeedCap);
        }

        // Once slow-start exits, BDP governs the pipeline
        if (hasExitedSlowStart)
            return optimal;

        // During slow-start, use the larger of BDP and slow-start window
        return Math.Max(optimal, slowStartLimit);
    }

    // --- Block Receipt Handling ---

    /// <summary>
    /// Called when a block is received from a peer. Updates pending tracking,
    /// slow-start window, and records delivery timing.
    /// Returns the PendingBlock that was removed (if any), for upstream use.
    /// </summary>
    public PendingBlock? OnBlockReceived(BlockRequest request, IPeerConnection peer)
    {
        PendingBlock? pending = null;

        // Remove from pending and decrement per-peer count
        if (_pendingBlocks.TryRemove(request, out pending))
        {
            if (pending.Peer != null)
            {
                if (_pendingCountByPeer.TryGetValue(pending.Peer, out var box))
                {
                    var newVal = Interlocked.Decrement(ref box.Value);
                    if (newVal < 0) Interlocked.Exchange(ref box.Value, 0);
                }
            }
        }

        // Record block delivery time for adaptive timeout
        if (pending != null)
        {
            var elapsed = DateTime.UtcNow - pending.RequestedAt;
            var timing = _peerBlockTiming.GetOrAdd(peer, _ => new PeerBlockTiming());
            timing.RecordBlockDelivery(elapsed);
        }

        // Update RTT measurement for dynamic pipeline depth
        if (peer is PeerConnection peerConnection)
        {
            peerConnection.UpdateRtt();
        }

        // Increment slow-start window only if peer hasn't exited slow-start
        if (!_slowStartExited.GetValueOrDefault(peer, false))
        {
            _slowStartWindow.AddOrUpdate(peer,
                InitialSlowStartWindow + 1,
                (_, current) => current + 1);

            // Check exit condition: rate increase < 10 KB/s compared to last check
            var currentRate = _statisticsTracker.GetPeerDownloadRate(peer);
            var lastRate = _lastPeerRate.GetValueOrDefault(peer, 0.0);
            _lastPeerRate[peer] = currentRate;

            if (lastRate > 0 && currentRate - lastRate < SlowStartExitThresholdBytesPerSec)
            {
                _slowStartExited[peer] = true;
                LogPeerExitedSlowStart(_logger, peer.PeerInfo.EndPoint,
                    _slowStartWindow.GetValueOrDefault(peer, InitialSlowStartWindow), currentRate);
            }
        }

        return pending;
    }

    // --- Timeout / Snub Checking ---

    /// <summary>
    /// Check and free all expired pending blocks.
    /// </summary>
    public async Task CheckTimeoutsAsync(
        ConcurrentDictionary<int, PieceBlockTracker> inProgressPieces,
        bool isEndgameMode,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var expiredBlocks = new List<(BlockRequest request, PendingBlock pending)>();

        foreach (var kvp in _pendingBlocks)
        {
            var elapsed = now - kvp.Value.RequestedAt;
            bool endgame = isEndgameMode;

            var peerTimeout = kvp.Value.Peer != null
                && _peerBlockTiming.TryGetValue(kvp.Value.Peer, out var timing)
                ? timing.GetAdaptiveTimeout(endgame)
                : endgame ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(15);

            if (elapsed <= peerTimeout) continue;

            var peer = kvp.Value.Peer;
            if (peer == null) continue;

            expiredBlocks.Add((kvp.Key, kvp.Value));
        }

        // Free ALL expired blocks at once (not 1 per peer per tick)
        foreach (var (request, pending) in expiredBlocks)
        {
            if (_pendingBlocks.TryRemove(request, out _))
            {
                if (pending.Peer != null && _pendingCountByPeer.TryGetValue(pending.Peer, out var timeoutBox))
                {
                    var timeoutVal = Interlocked.Decrement(ref timeoutBox.Value);
                    if (timeoutVal < 0) Interlocked.Exchange(ref timeoutBox.Value, 0);
                }

                if (inProgressPieces.TryGetValue(request.PieceIndex, out var progress))
                {
                    progress.MarkBlockNotRequested(request.Begin);
                }
            }
        }

        if (expiredBlocks.Count > 0)
        {
            var uniquePeerCount = 0;
            HashSet<IPeerConnection>? uniquePeers = null;
            foreach (var (_, pending) in expiredBlocks)
            {
                uniquePeers ??= new HashSet<IPeerConnection>(ReferenceEqualityComparer.Instance);
                if (uniquePeers.Add(pending.Peer))
                    uniquePeerCount++;
            }
            _logger.LogWarning("Timed out {Count} expired blocks from {Peers} peers",
                expiredBlocks.Count, uniquePeerCount);
        }
    }

    /// <summary>
    /// Marks peers as snubbed if no data received for snubThresholdSeconds.
    /// Snubbed peers' blocks are released for re-request to other peers.
    /// </summary>
    public void CheckSnubbedPeers(
        DateTime now,
        ConcurrentDictionary<IPeerConnection, DateTime> lastDataReceived,
        ConcurrentDictionary<int, PieceBlockTracker> inProgressPieces,
        int snubThresholdSeconds)
    {
        foreach (var (peer, lastData) in lastDataReceived)
        {
            if (!peer.IsConnected) continue;

            if ((now - lastData).TotalSeconds < snubThresholdSeconds)
            {
                // Clear snub flag if peer is delivering data again
                if (peer.IsSnubbed) peer.IsSnubbed = false;
                continue;
            }

            // Mark peer as snubbed (libtorrent pattern)
            if (!peer.IsSnubbed)
            {
                peer.IsSnubbed = true;
                _logger.LogWarning("Peer {Peer} marked as snubbed (no data for {Threshold}s)",
                    peer.PeerInfo?.EndPoint, snubThresholdSeconds);
            }

            // Release all pending blocks from this peer
            var blocksFromPeer = new List<BlockRequest>();
            foreach (var kvp in _pendingBlocks)
            {
                if (kvp.Value.Peer == peer)
                    blocksFromPeer.Add(kvp.Key);
            }

            if (blocksFromPeer.Count == 0) continue;

            foreach (var block in blocksFromPeer)
            {
                if (_pendingBlocks.TryRemove(block, out _))
                {
                    if (inProgressPieces.TryGetValue(block.PieceIndex, out var progress))
                        progress.MarkBlockNotRequested(block.Begin);
                }
            }

            if (_pendingCountByPeer.TryGetValue(peer, out var snubBox))
                Interlocked.Exchange(ref snubBox.Value, 0);

            lastDataReceived[peer] = now; // Reset to avoid repeated snub processing

            _logger.LogWarning("Peer {Peer} snubbed (no data for {Threshold}s), released {Count} blocks",
                peer.PeerInfo?.EndPoint, snubThresholdSeconds, blocksFromPeer.Count);
        }
    }

    // --- Cleanup ---

    /// <summary>
    /// Clear all per-peer tracking (called on stop/pause).
    /// </summary>
    public void ClearPeerTracking()
    {
        _pendingCountByPeer.Clear();
        _slowStartWindow.Clear();
        _slowStartExited.Clear();
        _lastPeerRate.Clear();
        _cachedPeerBitfields.Clear();
    }

    /// <summary>
    /// Remove tracking for a disconnected peer.
    /// </summary>
    public void OnPeerDisconnected(IPeerConnection peer)
    {
        _pendingCountByPeer.TryRemove(peer, out _);
        _slowStartWindow.TryRemove(peer, out _);
        _slowStartExited.TryRemove(peer, out _);
        _lastPeerRate.TryRemove(peer, out _);
        _cachedPeerBitfields.TryRemove(peer, out _);
    }

    /// <summary>
    /// Invalidates the cached Bitfield wrapper for a peer so that the next
    /// GetOrAdd call in RequestBlocksFromPeerWithCountAsync recreates it from
    /// the peer's current PeerBitfield byte array. Call this whenever the peer's
    /// bitfield changes (Have / Bitfield / HaveAll messages).
    /// </summary>
    internal void InvalidateCachedBitfield(IPeerConnection peer)
    {
        _cachedPeerBitfields.TryRemove(peer, out _);
    }

    // --- Source-generated logging (zero allocation when level disabled) ---

    [LoggerMessage(Level = LogLevel.Trace, Message = "Requested {Count} blocks from {Peer} (pipeline: {Pipeline}/{Max}, tracked: {Tracked})")]
    private static partial void LogRequestedBlocks(ILogger logger, int count, object peer, int pipeline, int max, int tracked);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Peer {Peer} exited slow-start at window={Window}, rate={Rate:F0} B/s")]
    private static partial void LogPeerExitedSlowStart(ILogger logger, object peer, int window, double rate);
}
