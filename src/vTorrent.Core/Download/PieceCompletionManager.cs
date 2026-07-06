using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Events;
using vTorrent.Abstractions.Models;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Engine;
using vTorrent.Core.Interfaces;
using vTorrent.Core.PeerCommunication;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Utilities;
using vTorrent.Core.PieceIO;
using vTorrent.Core.Session;
using vTorrent.Core.Streaming;

namespace vTorrent.Core.Download;

/// <summary>
/// Handles piece hash verification, disk writes, trust point management,
/// and smart banning. Extracted from DownloadCoordinator (Phase 4 refactor).
/// </summary>
internal sealed partial class PieceCompletionManager
{
    private readonly ILogger _logger;
    private readonly IPieceManager _pieceManager;
    private readonly IStatisticsTracker _statisticsTracker;
    private readonly IEndgameStrategy _endgameStrategy;
    private readonly IPeerRegistry _peerRegistry;
    private readonly TorrentInfo _torrentInfo;
    private readonly PeerSettings _settings;
    private readonly DiskWriteCache _diskWriteCache;
    private readonly int _blockSize;

    // Piece contributor tracking for trust points (libtorrent-style)
    private readonly ConcurrentDictionary<int, HashSet<IPeerConnection>> _pieceContributors = new();
    private readonly object _contributorsLock = new();

    // Smart ban: per-block hash tracking to identify corrupt peers
    private readonly SmartBanTracker _smartBan = new();

    // In-progress pieces (shared with DownloadCoordinator)
    private readonly ConcurrentDictionary<int, PieceBlockTracker> _inProgressPieces;

    // Consecutive disk write failure tracking
    private int _consecutiveWriteFailures;

    // Download completion fire-once gate
    private int _downloadCompletedFired;

    // Reference to WebSeedManager for failure notifications
    private WebSeedManager? _webSeedManager;

    // Disk write throttler for backpressure (wired after construction)
    private DiskWriteThrottler? _throttler;

    // Verification pipeline for offloading hash verification (wired after construction)
    private PieceVerificationPipeline? _verificationPipeline;

    // Events
    public event EventHandler<PieceCompletedEventArgs>? PieceCompleted;
    public event EventHandler? DownloadCompleted;
    public event EventHandler<DiskErrorEventArgs>? DiskWriteError;

    /// <summary>
    /// Fired when a piece fails hash verification and is no longer available.
    /// Subscribers (TorrentEngine) use this to broadcast DONTHAVE to peers.
    /// </summary>
    public event Action<int>? PieceLost;

    public PieceCompletionManager(
        IPieceManager pieceManager,
        IStatisticsTracker statisticsTracker,
        IEndgameStrategy endgameStrategy,
        IPeerRegistry peerRegistry,
        TorrentInfo torrentInfo,
        PeerSettings settings,
        DiskWriteCache diskWriteCache,
        ConcurrentDictionary<int, PieceBlockTracker> inProgressPieces,
        ILogger logger)
    {
        _pieceManager = pieceManager ?? throw new ArgumentNullException(nameof(pieceManager));
        _statisticsTracker = statisticsTracker ?? throw new ArgumentNullException(nameof(statisticsTracker));
        _endgameStrategy = endgameStrategy ?? throw new ArgumentNullException(nameof(endgameStrategy));
        _peerRegistry = peerRegistry; // Optional — trust points disabled if null
        _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(torrentInfo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _diskWriteCache = diskWriteCache ?? throw new ArgumentNullException(nameof(diskWriteCache));
        _inProgressPieces = inProgressPieces ?? throw new ArgumentNullException(nameof(inProgressPieces));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _blockSize = PeerConstants.BlockSize;
    }

    // --- Properties ---

    public ConcurrentDictionary<int, PieceBlockTracker> InProgressPieces => _inProgressPieces;
    public SmartBanTracker SmartBan => _smartBan;
    public long BytesInProgress => _diskWriteCache.TotalCachedBytes;

    public void SetWebSeedManager(WebSeedManager? manager) => _webSeedManager = manager;
    public void SetThrottler(DiskWriteThrottler? throttler) => _throttler = throttler;
    public void SetVerificationPipeline(PieceVerificationPipeline? pipeline) => _verificationPipeline = pipeline;
    public void ResetDownloadCompletedFired() => Interlocked.Exchange(ref _downloadCompletedFired, 0);

    // --- Contributor Tracking ---

    public void TrackPieceContributor(int pieceIndex, IPeerConnection peer)
    {
        var contributors = _pieceContributors.GetOrAdd(pieceIndex, _ => new HashSet<IPeerConnection>());
        lock (_contributorsLock)
        {
            contributors.Add(peer);
        }
    }

    public HashSet<IPeerConnection> GetAndClearContributors(int pieceIndex)
    {
        if (_pieceContributors.TryRemove(pieceIndex, out var contributors))
        {
            return contributors;
        }
        return new HashSet<IPeerConnection>();
    }

    // --- Piece Completion ---

    /// <summary>
    /// Complete a piece: hash verify, disk write, state commit.
    /// Called when all blocks of a piece have been written to cache.
    /// </summary>
    public async Task CompletePieceAsync(
        int pieceIndex,
        PieceBlockTracker tracker,
        PieceSelectionCoordinator pieceSelection,
        IPeerManager peerManager,
        WriteBatcher writeBatcher,
        IStreamingManager? streamingManager,
        Action signalPeerAvailable)
    {
        // Gate: only one thread can complete a piece. TryRemove is atomic.
        if (!_inProgressPieces.TryRemove(pieceIndex, out _))
            return;

        int pieceSize = (int)tracker.PieceSize;

        try
        {
            // Step 1: Transition to Finished state (not pickable, not completed)
            pieceSelection.PiecePicker.MarkFinished(pieceIndex);

            // Step 2: Protect cache entry from LRU eviction during hash + write
            _diskWriteCache.ProtectPiece(pieceIndex);

            // Step 3: Get piece data from cache
            var rawBuffer = _diskWriteCache.GetPieceData(pieceIndex);

            if (rawBuffer == null)
            {
                _logger.LogWarning("Piece {Piece} cache miss in CompletePieceAsync, restoring", pieceIndex);
                pieceSelection.PiecePicker.RestorePiece(pieceIndex);
                var freshTracker = new PieceBlockTracker(pieceIndex, pieceSize, _blockSize);
                _inProgressPieces[pieceIndex] = freshTracker;
                signalPeerAvailable();
                return;
            }

            var pieceData = rawBuffer.Length == pieceSize
                ? rawBuffer
                : rawBuffer.AsSpan(0, pieceSize).ToArray();

            var contributors = GetAndClearContributors(pieceIndex);

            // Step 4: Hash verification (offloaded to pipeline if available, else Task.Run fallback)
            bool isValid;
            if (_verificationPipeline != null)
            {
                isValid = await _verificationPipeline.VerifyPieceAsync(pieceIndex, pieceData).ConfigureAwait(false);
            }
            else
            {
                isValid = await Task.Run(() => _pieceManager.VerifyPiece(pieceIndex, pieceData)).ConfigureAwait(false);
            }

            if (!isValid)
            {
                _logger.LogWarning("Piece {Piece} failed hash verification, restoring to picker", pieceIndex);
                _statisticsTracker.RecordFailedBytes(pieceSize);

                pieceSelection.PiecePicker.RestorePiece(pieceIndex);
                _diskWriteCache.DiscardPiece(pieceIndex);
                pieceSelection.FileProgressTracker?.OnPieceFailed(pieceIndex);

                var freshTracker = new PieceBlockTracker(pieceIndex, pieceSize, _blockSize);
                _inProgressPieces[pieceIndex] = freshTracker;
                signalPeerAvailable();

                if (_endgameStrategy is EndgameManager em)
                    em.ClearPieceBlocks(pieceIndex, _blockSize, pieceSize);

                _smartBan.OnPieceFailed(pieceIndex);
                UpdateTrustPointsOnFailure(pieceIndex, contributors);
                PieceLost?.Invoke(pieceIndex);
                return;
            }

            // Step 5: Wait for disk write throttler backpressure before writing
            if (_throttler != null)
                await _throttler.WaitIfThrottledAsync(pieceData.Length, default).ConfigureAwait(false);

            // Step 5b: Write to disk
            var writeResult = await _pieceManager.WritePieceAsync(pieceIndex, pieceData,
                default, skipVerification: true).ConfigureAwait(false);

            if (!writeResult.IsSuccess)
            {
                _logger.LogError("Disk write failed for piece {Piece}: {Error}. Restoring for re-download.",
                    pieceIndex, writeResult.ErrorMessage);

                DiskWriteError?.Invoke(this, new DiskErrorEventArgs(pieceIndex,
                    writeResult.ErrorMessage ?? "Unknown write error"));

                pieceSelection.PiecePicker.RestorePiece(pieceIndex);
                _diskWriteCache.DiscardPiece(pieceIndex);

                var retryTracker = new PieceBlockTracker(pieceIndex, pieceSize, _blockSize);
                _inProgressPieces[pieceIndex] = retryTracker;
                signalPeerAvailable();
                return;
            }

            // Step 6: State commit
            Interlocked.Exchange(ref _consecutiveWriteFailures, 0);
            pieceSelection.LocalBitfield.SetPiece(pieceIndex);
            pieceSelection.PiecePicker.MarkCompleted(pieceIndex);
            _diskWriteCache.ReleasePiece(pieceIndex);
            _statisticsTracker.RecordPieceCompleted();

            pieceSelection.ClearWantedBit(pieceIndex);

            // Notify streaming manager
            var wasTimeCritical = streamingManager?.OnPieceCompleted(pieceIndex) ?? false;

            // O(1) wanted-complete counter
            if (pieceSelection.FileProgressTracker != null && pieceSelection.FileProgressTracker.IsPieceWanted(pieceIndex))
                pieceSelection.IncrementWantedHaveCount();

            _statisticsTracker.RecordVerifiedDownload(pieceSize);
            UpdateTrustPointsOnSuccess(pieceIndex, contributors);

            LogPieceCompleted(_logger, pieceIndex, pieceSelection.PiecesCompleted, pieceSelection.TotalPieces);

            // Broadcast HAVE
            var haveMsg = PeerMessage.CreateHave(pieceIndex);
            foreach (var peer in peerManager.ConnectedPeers)
            {
                if (peer.IsConnected)
                    writeBatcher.QueueMessage(peer, haveMsg);
            }

            // Fire PieceCompleted event
            if (PieceCompleted != null)
            {
                var args = new PieceCompletedEventArgs(pieceIndex, pieceSelection.PiecesCompleted, pieceSelection.TotalPieces);
                Task.Run(() => PieceCompleted.Invoke(this, args));
            }

            // Fire DownloadCompleted eagerly — but ONLY if no pieces are still in progress.
            // This prevents the race where DownloadCompleted triggers ReleaseWriteHandlesAsync
            // while other pieces still have pending disk writes.
            if (pieceSelection.IsWantedComplete
                && _inProgressPieces.IsEmpty
                && Interlocked.CompareExchange(ref _downloadCompletedFired, 1, 0) == 0)
            {
                _logger.LogDebug("Download completed!");
                DownloadCompleted?.Invoke(this, EventArgs.Empty);
            }

            // Time-critical pieces still need synchronous flush
            if (wasTimeCritical)
                await _pieceManager.FlushPieceAsync(pieceIndex).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception completing piece {Piece}, restoring", pieceIndex);

            int state = pieceSelection.PiecePicker.GetPieceState(pieceIndex);

            if (state == 3)
                pieceSelection.PiecePicker.RestorePiece(pieceIndex);
            else if (state == 1)
                pieceSelection.PiecePicker.MarkNotStarted(pieceIndex);

            _diskWriteCache.DiscardPiece(pieceIndex);

            if (state != 2)
            {
                var restoreSize = pieceSize > 0 ? pieceSize : pieceSelection.GetPieceSize(pieceIndex);
                var freshTracker = new PieceBlockTracker(pieceIndex, restoreSize, _blockSize);
                _inProgressPieces[pieceIndex] = freshTracker;
                signalPeerAvailable();
            }
        }
    }

    // --- Repair & Diagnostics ---

    /// <summary>
    /// Detect blocks stuck in state 1 (requested) with no corresponding _pendingBlocks entry.
    /// Reset them to state 0 (free) so they can be re-requested.
    /// libtorrent equivalent: piece_picker::check_peers in on_tick().
    /// </summary>
    public int RepairOrphanedBlocks(
        ConcurrentDictionary<BlockRequest, PendingBlock> pendingBlocks,
        PieceSelectionCoordinator pieceSelection,
        bool isEndgameMode,
        Action signalPeerAvailable)
    {
        int totalRepaired = 0;

        foreach (var (pieceIndex, tracker) in _inProgressPieces)
        {
            var requestedBlocks = tracker.GetRequestedNotReceivedBlocks();
            if (requestedBlocks.Length == 0) continue;

            foreach (var block in requestedBlocks)
            {
                if (!pendingBlocks.ContainsKey(block))
                {
                    tracker.MarkBlockNotRequested(block.Begin);
                    totalRepaired++;
                }
            }
        }

        if (totalRepaired > 0)
        {
            _logger.LogWarning("RepairOrphanedBlocks: reset {Count} orphaned blocks to free", totalRepaired);
            signalPeerAvailable();
        }

        // Phase 2: Force-start Available pieces that have availability but aren't in-progress.
        if (isEndgameMode)
        {
            int forceStarted = 0;
            int total = pieceSelection.TotalPieces;

            for (int i = 0; i < total; i++)
            {
                if (pieceSelection.LocalBitfield.HasPiece(i)) continue;
                if (_inProgressPieces.ContainsKey(i)) continue;

                int state = pieceSelection.PiecePicker.GetPieceState(i);
                if (state != 0) continue; // Only Available pieces

                int avail = pieceSelection.PiecePicker.GetPieceAvailability(i);
                if (avail <= 0) continue;

                lock (pieceSelection.PieceLock)
                {
                    if (pieceSelection.PiecePicker.GetPieceState(i) != 0) continue;
                    if (_inProgressPieces.ContainsKey(i)) continue;

                    pieceSelection.PiecePicker.MarkInProgress(i);
                    var pieceSize = pieceSelection.GetPieceSize(i);
                    var tracker = new PieceBlockTracker(i, pieceSize, _blockSize);
                    _inProgressPieces[i] = tracker;
                    forceStarted++;
                }
            }

            if (forceStarted > 0)
            {
                _logger.LogWarning("RepairOrphanedBlocks: force-started {Count} available pieces stuck outside pipeline", forceStarted);
                signalPeerAvailable();
            }
        }

        return totalRepaired;
    }

    /// <summary>
    /// Diagnostic: log state of remaining pieces when download is near completion.
    /// </summary>
    public void DiagnoseStuckPieces(
        PieceSelectionCoordinator pieceSelection,
        ConcurrentDictionary<BlockRequest, PendingBlock> pendingBlocks,
        IPeerManager peerManager,
        DiskWriteCache diskWriteCache)
    {
        int completed = pieceSelection.PiecesCompleted;
        int total = pieceSelection.TotalPieces;

        if (total == 0) return;

        double pct = (double)completed / total;
        if (pct < 0.95 || pieceSelection.IsWantedComplete) return;

        int remaining = total - completed;
        int inProgress = _inProgressPieces.Count;
        int pending = pendingBlocks.Count;

        int availablePeers = 0;
        foreach (var p in peerManager.ConnectedPeers)
            if (p.IsConnected && !p.IsChoked) availablePeers++;

        var missingInfo = new System.Text.StringBuilder();
        int missingCount = 0;

        for (int i = 0; i < total && missingCount < 20; i++)
        {
            if (!pieceSelection.LocalBitfield.HasPiece(i))
            {
                int pickerState = pieceSelection.PiecePicker.GetPieceState(i);
                bool isInProgress = _inProgressPieces.ContainsKey(i);
                bool hasCacheData = diskWriteCache.HasPieceData(i);

                string stateStr = pickerState switch
                {
                    0 => "Available",
                    1 => "InProgress",
                    2 => "Completed(!)",
                    3 => "Finished",
                    _ => $"Unknown({pickerState})"
                };

                int pendingForPiece = 0;
                foreach (var kvp in pendingBlocks)
                    if (kvp.Key.PieceIndex == i) pendingForPiece++;

                string trackerInfo = "";
                if (_inProgressPieces.TryGetValue(i, out var tracker))
                {
                    int receivedBlocks = 0;
                    var freeOffsets = new List<int>();
                    var requestedOffsets = new List<int>();

                    for (int b = 0; b < tracker.BlockCount; b++)
                    {
                        if (tracker.IsBlockReceived(b))
                            receivedBlocks++;
                        else
                        {
                            int offset = b * _blockSize;
                            bool isFree = false;
                            foreach (var ub in tracker.GetAllUnrequestedBlocks())
                                if (ub.Begin == offset) { isFree = true; break; }
                            if (isFree) freeOffsets.Add(b);
                            else requestedOffsets.Add(b);
                        }
                    }

                    trackerInfo = $" tracker[{receivedBlocks}/{tracker.BlockCount} rcvd, free=[{string.Join(",", freeOffsets)}], req=[{string.Join(",", requestedOffsets)}], complete={tracker.IsComplete}]";
                }

                int peersWithPiece = 0;
                foreach (var p in peerManager.ConnectedPeers)
                    if (p.IsConnected && pieceSelection.PeerHasPiece(p, i)) peersWithPiece++;

                int pickerAvail = pieceSelection.PiecePicker.GetPieceAvailability(i);

                missingInfo.Append($"\n  piece {i}: picker={stateStr}, avail={pickerAvail}, peersHave={peersWithPiece}, inDict={isInProgress}, cached={hasCacheData}, pending={pendingForPiece}{trackerInfo}");
                missingCount++;
            }
        }

        _logger.LogWarning(
            "DIAG stuck@{Pct:F1}%: {Completed}/{Total} done, remaining={Remaining}, " +
            "inProgress={InProgress}, pending={Pending}, peers={Peers}, " +
            "wantedHave={WantedHave}/{WantedTotal}, endgame={Endgame}{Missing}",
            pct * 100, completed, total, remaining,
            inProgress, pending, availablePeers,
            0, 0, false, // Note: wantedHave/wantedTotal accessed via pieceSelection.IsWantedComplete
            missingInfo.ToString());
    }

    // --- Trust Points ---

    private void UpdateTrustPointsOnSuccess(int pieceIndex, HashSet<IPeerConnection> contributors)
    {
        if (_peerRegistry == null || contributors.Count == 0)
            return;

        foreach (var peer in contributors)
        {
            try
            {
                var key = PeerRegistry.GetPeerKey(peer.PeerInfo);
                if (_peerRegistry.TryGetPeer(key, out var state) && state.Score != null)
                {
                    state.Score.OnValidPiece();
                    LogPeerCreditedForPiece(_logger, peer.PeerInfo.EndPoint, pieceIndex, state.Score.TrustPoints);
                }
            }
            catch (Exception ex)
            {
                LogTrustPointUpdateError(_logger, ex, peer.PeerInfo?.EndPoint);
            }
        }
    }

    private void UpdateTrustPointsOnFailure(int pieceIndex, HashSet<IPeerConnection> contributors)
    {
        if (_peerRegistry == null || contributors.Count == 0)
            return;

        foreach (var peer in contributors)
        {
            try
            {
                var key = PeerRegistry.GetPeerKey(peer.PeerInfo);
                if (_peerRegistry.TryGetPeer(key, out var state) && state.Score != null)
                {
                    bool shouldBan = state.Score.OnInvalidPiece();
                    _logger.LogWarning("Peer {Peer} blamed for invalid piece {Piece} (trust: {Trust}, parole: {Parole})",
                        peer.PeerInfo.EndPoint, pieceIndex, state.Score.TrustPoints, state.Score.OnParole);

                    if (shouldBan)
                    {
                        _logger.LogWarning("Auto-banning peer {Peer} for repeated hash failures (trust: {Trust})",
                            peer.PeerInfo.EndPoint, state.Score.TrustPoints);
                        _peerRegistry.Ban(key, TimeSpan.FromHours(24), "Auto-ban: Trust points depleted from hash failures");
                        _ = peer.DisconnectAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                LogTrustPointUpdateError(_logger, ex, peer.PeerInfo?.EndPoint);
            }
        }

        // Notify WebSeedManager for its own ban/backoff tracking
        if (_webSeedManager != null)
        {
            foreach (var contributor in contributors)
                _webSeedManager.OnPieceFailed(pieceIndex, contributor);
        }
    }

    // --- Source-generated logging (zero allocation when level disabled) ---

    [LoggerMessage(Level = LogLevel.Debug, Message = "Completed piece {PieceIndex} ({Completed}/{Total})")]
    private static partial void LogPieceCompleted(ILogger logger, int pieceIndex, int completed, int total);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Peer {Peer} credited for valid piece {PieceIndex} (trust: {Trust})")]
    private static partial void LogPeerCreditedForPiece(ILogger logger, object peer, int pieceIndex, int trust);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error updating trust points for peer {Peer}")]
    private static partial void LogTrustPointUpdateError(ILogger logger, Exception exception, object? peer);
}
