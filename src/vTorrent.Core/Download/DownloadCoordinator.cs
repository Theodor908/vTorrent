using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.PeerCommunication.Events;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Utilities;
using vTorrent.Core.PeerCommunication;
using vTorrent.Core.PieceIO;
using vTorrent.Core.Interfaces;
using vTorrent.Core.Session;
using vTorrent.Core.Streaming;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Events;
using vTorrent.Abstractions.Models;
using vTorrent.Core.Engine;

namespace vTorrent.Core.Download;

/// <summary>
/// Download loop, message handlers, peer signaling, lifecycle.
/// Composes PieceSelectionCoordinator, RequestDispatcher, and PieceCompletionManager.
/// </summary>
public partial class DownloadCoordinator : IMessageHandler, IDisposable
{
    private readonly ILogger<DownloadCoordinator> _logger;
    private readonly IPeerManager _peerManager;
    private readonly IPieceManager _pieceManager;
    private readonly IStatisticsTracker _statisticsTracker;
    private readonly IEndgameStrategy _endgameStrategy;
    private readonly TorrentInfo _torrentInfo;
    private readonly PeerSettings _settings;
    private readonly IOptionsMonitor<BehaviorSettings>? _behaviorMonitor;
    private readonly IOptionsMonitor<DiskSettings>? _diskMonitor;
    private readonly IOptionsMonitor<WebSeedSettings>? _webSeedMonitor;
    private readonly WriteBatcher _writeBatcher = new();
    private readonly DiskWriteCache _diskWriteCache;
    private readonly int _blockSize;

    // Composed sub-coordinators (created internally, NOT via DI)
    private readonly PieceSelectionCoordinator _pieceSelection;
    private readonly RequestDispatcher _requestDispatcher;
    private readonly PieceCompletionManager _pieceCompletion;

    // In-progress pieces (shared across sub-coordinators)
    private readonly ConcurrentDictionary<int, PieceBlockTracker> _inProgressPieces = new();

    // Download loop state
    private readonly SemaphoreSlim _peerAvailableSignal = new(0, 1);
    private readonly List<IPeerConnection> _availablePeers = new();
    private CancellationTokenSource _stopCts = new();

    // Reusable buffers to avoid per-tick allocations in download loop
    private readonly List<KeyValuePair<int, PieceBlockTracker>> _inProgressSnapshotBuffer = new();
    private Task<int>[] _peerTaskBuffer = Array.Empty<Task<int>>();
    private Task _downloadTask;
    private bool _isRunning;
    private bool _disposed;
    private volatile bool _cancelNonCriticalPending;

    // Atomic seed counter (incremented on seed transition, decremented on seed disconnect)
    private int _connectedSeedCount;

    // Pipeline tick safety net
    private PipelineTick? _pipelineTick;

    // Progress throttle
    private long _lastProgressReportTicks;
    private const long ProgressReportIntervalTicks = 250 * TimeSpan.TicksPerMillisecond;

    // Snub detection
    private readonly ConcurrentDictionary<IPeerConnection, DateTime> _lastDataReceived = new();
    private readonly int _snubThresholdSeconds;

    // Streaming manager
    private IStreamingManager? _streamingManager;
    private WebSeedManager? _webSeedManager;
    private PeerMessageRouter? _messageRouter;

    // Events (delegated from PieceCompletionManager + own events)
    public event EventHandler<PieceCompletedEventArgs> PieceCompleted;
    public event EventHandler<DownloadProgressEventArgs> ProgressChanged;
    public event EventHandler DownloadCompleted;
    public event EventHandler<DiskErrorEventArgs> DiskWriteError;

    // --- Properties (delegate to sub-coordinators) ---

    /// <summary>BEP 54: exposes PieceCompletionManager so callers can subscribe to PieceLost.</summary>
    internal PieceCompletionManager PieceCompletionManager => _pieceCompletion;

    public bool IsRunning => _isRunning;
    public bool IsComplete => _pieceSelection.IsComplete;
    public bool IsWantedComplete => _pieceSelection.IsWantedComplete;

    /// <summary>
    /// Whether file priorities are active (selective download).
    /// </summary>
    public bool HasFilePriorities => _pieceSelection.HasFilePriorities;

    /// <summary>
    /// Check if all wanted pieces are present in the given bitfield.
    /// Used by post-verification state evaluation.
    /// </summary>
    public bool AreWantedPiecesComplete(Bitfield bitfield) => _pieceSelection.AreWantedPiecesComplete(bitfield);

    public long BytesDownloaded => _statisticsTracker.TotalDownloaded;
    public double DownloadRate => _statisticsTracker.DownloadRate;
    public int PiecesCompleted => _pieceSelection.PiecesCompleted;
    public int TotalPieces => _pieceSelection.TotalPieces;
    public double Progress => _pieceSelection.Progress;
    public long BytesRemaining => _torrentInfo.TotalSize - (_pieceSelection.PiecesCompleted * _torrentInfo.PieceLength);
    public int PendingRequests => _requestDispatcher.PendingRequestCount;
    public int InProgressPieces => _inProgressPieces.Count;
    public bool IsSequentialMode => _pieceSelection.IsSequentialMode;
    public bool IsStreaming => _pieceSelection.IsStreaming;
    public long EndgameWastedBytes => _endgameStrategy.WastedBytes;
    public int EndgameDuplicateBlocks => _endgameStrategy.DuplicateBlockCount;
    public long BytesInProgress => _diskWriteCache.TotalCachedBytes;
    public long BytesEffective => BytesDownloaded + BytesInProgress;

    /// <summary>
    /// Endgame mode activates only when there are no un-requested wanted blocks left to
    /// pick — i.e. every remaining block is already in flight, so the only way to make
    /// progress is to request a block a second time from another peer. This mirrors
    /// libtorrent's request_a_block(), which enters end-game solely because the picker
    /// could not find enough fresh (non-busy) blocks, never on a "pieces touched" count.
    /// </summary>
    public bool IsEndgameMode => ComputeEndgame(_inProgressPieces.Values, PiecesCompleted, TotalPieces);

    /// <summary>
    /// Pure endgame decision (block-level, libtorrent-aligned). Endgame is active iff:
    /// (a) no wanted piece is left completely untouched (every not-yet-completed piece is
    ///     already in progress, so the picker has no fresh piece to open), AND
    /// (b) every in-progress piece has all of its blocks already requested (no free block
    ///     remains anywhere). While any free block remains, normal rarest-first picking
    ///     still makes forward progress, so we are NOT in endgame.
    /// The old heuristic tripped on (a) alone (inProgress >= remaining), which fires at
    /// ~0% on a small/single-peer torrent because the picker opens one block in many
    /// pieces, prematurely forcing strict endgame and its duplicate-request flood.
    /// </summary>
    internal static bool ComputeEndgame(
        ICollection<PieceBlockTracker> inProgressPieces, int piecesCompleted, int totalPieces)
    {
        int inProgress = inProgressPieces.Count;
        if (inProgress == 0) return false;

        int remaining = totalPieces - piecesCompleted;

        // (a) An untouched wanted piece still exists — the picker can open it. Not endgame.
        if (inProgress < remaining) return false;

        // (b) Any in-progress piece with a free block can still be filled normally.
        foreach (var tracker in inProgressPieces)
        {
            if (tracker.HasUnrequestedBlocks())
                return false;
        }

        return true;
    }

    /// <summary>
    /// Count of connected peers that have all pieces (seeds).
    /// Maintained by an atomic counter updated on seed transition and disconnection.
    /// </summary>
    public int ConnectedSeeds => Volatile.Read(ref _connectedSeedCount);

    // --- BEP 52 ---

    private Interfaces.IPeerProber? _peerProber;
    private bool _lastEndgameNotified;

    public Interfaces.IPeerProber? PeerProber
    {
        set
        {
            _peerProber = value;
            _requestDispatcher.PeerProber = value;
        }
    }

    /// <summary>
    /// Forwards download-side endgame transitions to the peer prober so it can
    /// suspend slow-peer evaluation during endgame (duplicate blocks suppress
    /// payload rate). Edge-triggered: only fires on an actual state change.
    /// </summary>
    internal void NotifyProberEndgameTransition(bool endgame)
    {
        if (endgame == _lastEndgameNotified) return;
        _lastEndgameNotified = endgame;
        if (endgame)
            _peerProber?.EnterEndgameMode();
        else
            _peerProber?.ExitEndgameMode();
    }
    public HashPicker? HashPickerInstance { set => _requestDispatcher.HashPickerInstance = value; }

    // --- Public API (delegates to sub-coordinators) ---

    public void SetSequentialMode(bool enabled) => _pieceSelection.SetSequentialMode(enabled);
    public void SetAutoSequentialMode(bool enabled) => _pieceSelection.SetAutoSequentialMode(enabled);
    public void SetFileProgressTracker(FileProgressTracker tracker) => _pieceSelection.SetFileProgressTracker(tracker);
    public void SetFirstLastPiecePriority(bool enabled) => _pieceSelection.SetFirstLastPiecePriority(enabled);
    public void SetFilePriorities(FilePriority[] priorities) => _pieceSelection.SetFilePriorities(priorities);
    public void SetPrioritizePartialPieces(bool value) => _pieceSelection.SetPrioritizePartialPieces(value);
    public void SetStrictEndgameMode(bool strict) => _pieceSelection.SetStrictEndgameMode(strict);

    public void SetPieceDeadline(int pieceIndex, int deadlineMs, bool alertWhenAvailable = false)
    {
        if (_streamingManager == null) return;

        if (_pieceSelection.PiecePicker.IsPieceCompleted(pieceIndex))
        {
            if (alertWhenAvailable)
                _streamingManager.OnPieceCompleted(pieceIndex);
            return;
        }

        bool wasFirst = _streamingManager.SetPieceDeadline(pieceIndex, deadlineMs, alertWhenAvailable);
        if (wasFirst)
        {
            _cancelNonCriticalPending = true;
            _logger.LogDebug("Streaming mode activated — will cancel non-critical requests on next tick");
        }
    }

    public void ResetPieceDeadline(int pieceIndex)
    {
        _streamingManager?.ResetPieceDeadline(pieceIndex);
    }

    public void ClearPieceDeadlines()
    {
        _streamingManager?.ClearPieceDeadlines();
        _logger.LogDebug("All piece deadlines cleared");
    }

    public bool HasPiece(int pieceIndex) => _pieceSelection.LocalBitfield.HasPiece(pieceIndex);
    public byte[] GetBitfieldBytes() => _pieceSelection.LocalBitfield.Data;

    // --- Wiring ---

    internal void SetMessageRouter(PeerMessageRouter router) => _messageRouter = router;

    internal void SetWebSeedManager(WebSeedManager webSeedManager)
    {
        if (_webSeedManager != null && _messageRouter != null)
            _messageRouter.UnsubscribeFrom(_webSeedManager);

        _webSeedManager = webSeedManager;
        _pieceCompletion.SetWebSeedManager(webSeedManager);

        if (_webSeedManager != null && _messageRouter != null)
            _messageRouter.SubscribeTo(_webSeedManager);
    }

    internal void SetStreamingManager(IStreamingManager streamingManager)
    {
        _streamingManager = streamingManager;
        _pieceSelection.SetStreamingManager(streamingManager);
        streamingManager.PieceAvailable += OnTimeCriticalPieceAvailable;
    }

    private void OnTimeCriticalPieceAvailable(int pieceIndex)
    {
        LogTimeCriticalPieceAvailable(_logger, pieceIndex);
    }

    // --- Constructor ---

    public DownloadCoordinator(
        IPeerManager peerManager,
        IPieceManager pieceManager,
        IStatisticsTracker statisticsTracker,
        IEndgameStrategy endgameStrategy,
        Bitfield localBitfield,
        TorrentInfo torrentInfo,
        PeerSettings settings,
        IPeerRegistry peerRegistry,
        ILogger<DownloadCoordinator> logger,
        DiskWriteCache diskWriteCache = null,
        IOptionsMonitor<BehaviorSettings>? behaviorMonitor = null,
        IOptionsMonitor<DiskSettings>? diskMonitor = null,
        IOptionsMonitor<WebSeedSettings>? webSeedMonitor = null)
    {
        _peerManager = peerManager ?? throw new ArgumentNullException(nameof(peerManager));
        _pieceManager = pieceManager ?? throw new ArgumentNullException(nameof(pieceManager));
        _statisticsTracker = statisticsTracker ?? throw new ArgumentNullException(nameof(statisticsTracker));
        _endgameStrategy = endgameStrategy ?? throw new ArgumentNullException(nameof(endgameStrategy));
        _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(torrentInfo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _behaviorMonitor = behaviorMonitor;
        _diskMonitor = diskMonitor;
        _webSeedMonitor = webSeedMonitor;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _diskWriteCache = diskWriteCache ?? new DiskWriteCache();
        _blockSize = PeerConstants.BlockSize;
        _snubThresholdSeconds = settings.InactivityTimeout > 0 ? settings.InactivityTimeout : 600;

        // Create composed sub-coordinators
        _pieceSelection = new PieceSelectionCoordinator(
            torrentInfo, localBitfield, settings, endgameStrategy, pieceManager, logger, behaviorMonitor, diskMonitor);

        _requestDispatcher = new RequestDispatcher(
            torrentInfo, settings, statisticsTracker, logger);

        _pieceCompletion = new PieceCompletionManager(
            pieceManager, statisticsTracker, endgameStrategy,
            peerRegistry, torrentInfo, settings, _diskWriteCache,
            _inProgressPieces, logger);

        // Wire events from PieceCompletionManager to DownloadCoordinator surface
        _pieceCompletion.PieceCompleted += (s, e) => PieceCompleted?.Invoke(this, e);
        _pieceCompletion.DownloadCompleted += (s, e) => DownloadCompleted?.Invoke(this, e);
        _pieceCompletion.DiskWriteError += (s, e) => DiskWriteError?.Invoke(this, e);

        // Subscribe to peer disconnection events
        _peerManager.PeerDisconnected += OnPeerDisconnected;

        _logger.LogDebug("DownloadCoordinator created for {Name} - {Completed}/{Total} pieces",
            torrentInfo.Name, localBitfield.CompletePieces, torrentInfo.PieceCount);
    }

    // --- Peer Disconnect Handler ---

    private void OnPeerDisconnected(object sender, PeerDisconnectedEventArgs e)
    {
        var endpointKey = e.PeerInfo.EndPoint?.ToString();
        IPeerConnection disconnectedPeer = null;
        var blocksFromPeer = new List<BlockRequest>();

        foreach (var kvp in _requestDispatcher.PendingBlocks)
        {
            if (kvp.Value.Peer?.PeerInfo?.EndPoint?.Equals(e.PeerInfo.EndPoint) == true)
            {
                blocksFromPeer.Add(kvp.Key);
                disconnectedPeer ??= kvp.Value.Peer;
            }
        }

        disconnectedPeer ??= _peerManager.ConnectedPeers
            .FirstOrDefault(p => p.PeerInfo?.EndPoint?.Equals(e.PeerInfo.EndPoint) == true);

        foreach (var block in blocksFromPeer)
        {
            if (_requestDispatcher.PendingBlocks.TryRemove(block, out var pending))
            {
                if (_inProgressPieces.TryGetValue(block.PieceIndex, out var progress))
                    progress.MarkBlockNotRequested(block.Begin);
            }
        }

        // Reset orphaned blocks in PieceBlockTracker
        string peerId = e.PeerInfo.EndPoint?.ToString() ?? "";
        foreach (var kvp in _inProgressPieces)
        {
            kvp.Value.ResetBlocksForPeer(peerId);
        }

        if (disconnectedPeer != null)
        {
            if (disconnectedPeer.IsSeed) Interlocked.Decrement(ref _connectedSeedCount);
            _requestDispatcher.OnPeerDisconnected(disconnectedPeer);
            _lastDataReceived.TryRemove(disconnectedPeer, out _);
        }

        // CRITICAL FIX: Always decrement availability using cached bitfield.
        byte[] cachedBitfield = null;
        if (endpointKey != null)
            _requestDispatcher.PeerBitfieldCache.TryRemove(endpointKey, out cachedBitfield);

        var bitfieldToDecrement = cachedBitfield ?? disconnectedPeer?.PeerBitfield;
        if (bitfieldToDecrement != null)
        {
            _pieceSelection.PiecePicker.ApplyBitfield(bitfieldToDecrement, _torrentInfo.PieceCount, delta: -1);
        }

        if (_endgameStrategy is EndgameManager endgameManager && disconnectedPeer != null)
            endgameManager.OnPeerDisconnected(disconnectedPeer);

        if (blocksFromPeer.Count > 0)
        {
            LogPeerDisconnectedReleasedBlocks(_logger, e.PeerInfo.EndPoint, blocksFromPeer.Count);
        }

        SignalPeerAvailable();
    }

    // --- Lifecycle ---

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("DownloadCoordinator is already running");

        if (IsComplete)
        {
            _logger.LogDebug("Download already complete");
            return;
        }

        _isRunning = true;
        _pieceCompletion.ResetDownloadCompletedFired();

        // Pre-compute wanted bitfield for O(N/8) interest checks
        _pieceSelection.InitializeWantedBitfield();

        _stopCts = new CancellationTokenSource();

        _logger.LogDebug("Starting download - {Remaining} pieces remaining",
            TotalPieces - PiecesCompleted);

        // Start pipeline tick safety net
        _pipelineTick?.Dispose();
        _pipelineTick = new PipelineTick(
            _logger,
            getPendingRequests: () => _requestDispatcher.PendingRequestCount,
            getInProgressPieces: () => _inProgressPieces.Count,
            isComplete: () => IsWantedComplete);
        _pipelineTick.PipelineStalled += SignalPeerAvailable;
        _pipelineTick.Start();

        _downloadTask = Task.Run(() => DownloadLoopAsync(_stopCts.Token), cancellationToken);
    }

    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _logger.LogDebug("Stopping download...");

        _pipelineTick?.Stop();
        _isRunning = false;
        _stopCts.Cancel();

        if (_downloadTask != null)
        {
            try { await _downloadTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        // Cancel all pending requests
        foreach (var pending in _requestDispatcher.PendingBlocks.Values)
        {
            try
            {
                await pending.Peer.CancelBlockAsync(pending.PieceIndex, pending.Begin, pending.Length).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to cancel block request during stop for piece {Piece}", pending.PieceIndex);
            }
        }

        _requestDispatcher.PendingBlocks.Clear();

        // Reset requested flags on all in-progress pieces
        foreach (var (_, progress) in _inProgressPieces)
        {
            progress.ResetRequestedBlocks();
        }

        _requestDispatcher.ClearPeerTracking();

        _logger.LogDebug("Download stopped - {Completed}/{Total} pieces complete",
            PiecesCompleted, TotalPieces);
    }

    // --- Message Handler Registration ---

    public void RegisterHandlers(PeerMessageRouter router)
    {
        router.RegisterHandler(MessageType.Piece, HandlePieceAsync);
        router.RegisterHandler(MessageType.Have, HandleHaveAsync);
        router.RegisterHandler(MessageType.Bitfield, HandleBitfieldAsync);
        router.RegisterHandler(MessageType.HaveAll, HandleHaveAllAsync);
        router.RegisterHandler(MessageType.HaveNone, HandleHaveNoneAsync);
        router.RegisterHandler(MessageType.Choke, HandleChokeAsync);
        router.RegisterHandler(MessageType.Unchoke, HandleUnchokeAsync);
        router.RegisterHandler(MessageType.SuggestPiece, HandleSuggestPieceAsync);
        router.RegisterHandler(MessageType.RejectRequest, HandleRejectRequestAsync);
    }

    #region Message Handlers

    /// <summary>
    /// CRITICAL: HandlePieceAsync delegates to extracted classes but NEVER dispatches requests.
    /// Only the download loop dispatches. Inline dispatch causes concurrent lock races and stalls.
    /// </summary>
    public async Task HandlePieceAsync(IPeerConnection peer, PeerMessage message)
    {
        try
        {
            // Discard blocks that arrive after stop
            if (!_isRunning)
                return;

            // Record data receipt for snub detection
            _lastDataReceived[peer] = DateTime.UtcNow;

            message.ParsePieceSpan(out int pieceIndex, out int begin, out ReadOnlySpan<byte> blockSpan);
            int blockLength = blockSpan.Length;
            var blockData = BlockBufferPool.Rent(blockLength);
            blockSpan.CopyTo(blockData);
            var request = new BlockRequest(pieceIndex, begin, blockLength);

            try
            {
                // 1. RequestDispatcher: mark block delivered, update slow-start timing
                _requestDispatcher.OnBlockReceived(request, peer);

                // 2. Check for duplicate endgame blocks
                bool isDuplicate = _endgameStrategy.OnBlockReceived(request, peer);
                if (isDuplicate)
                {
                    _statisticsTracker.RecordEndgameWaste(blockLength);
                    LogDuplicateEndgameBlock(_logger, pieceIndex, begin);
                    return;
                }

                // Record payload download
                _statisticsTracker.RecordPayloadDownload(peer, blockLength);

                // 3. PieceCompletionManager: track contributor, smart ban
                _pieceCompletion.TrackPieceContributor(pieceIndex, peer);

                var banResult = _pieceCompletion.SmartBan.RecordBlock(pieceIndex, begin, blockData, peer);
                if (banResult.ShouldBanPeer)
                {
                    if (peer is WebSeedConnection && _webSeedMonitor?.CurrentValue.BanWebSeeds == false)
                    {
                        _logger.LogDebug("Skipping web seed ban for {Peer} — BanWebSeeds is disabled",
                            peer.PeerInfo.EndPoint);
                    }
                    else
                    {
                        _logger.LogWarning("Smart ban: peer {Peer} sent conflicting data for piece {Piece} offset {Offset}",
                            peer.PeerInfo.EndPoint, pieceIndex, begin);
                        _ = peer.DisconnectAsync();
                    }
                }

                LogBlockReceived(_logger, pieceIndex, begin, blockLength);

                // Store block in progress tracker
                if (!_inProgressPieces.TryGetValue(pieceIndex, out var progress))
                {
                    _logger.LogWarning("Received block for unknown piece {Piece}", pieceIndex);
                    return;
                }

                // CAS gate FIRST: prevents duplicate writes from corrupting cache data.
                if (!progress.MarkBlockReceived(begin))
                {
                    LogDuplicateBlock(_logger, pieceIndex, begin);
                    return;
                }

                // Write data to cache AFTER CAS gate succeeds
                _diskWriteCache.AddBlock(pieceIndex, progress.PieceSize, begin, blockData, blockLength);

                // Completion check
                if (progress.IncrementBlocksWritten() >= progress.BlockCount)
                {
                    await _pieceCompletion.CompletePieceAsync(
                        pieceIndex, progress, _pieceSelection, _peerManager,
                        _writeBatcher, _streamingManager, SignalPeerAvailable).ConfigureAwait(false);
                }

                ReportProgress();

                // 4. Signal peer available (stays in DownloadCoordinator)
                // NEVER dispatch new requests from here
            }
            finally
            {
                BlockBufferPool.Return(blockData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in HandlePieceAsync from {Peer}", peer.PeerInfo.EndPoint);
        }
    }

    public Task HandleHaveAsync(IPeerConnection peer, PeerMessage message)
    {
        var pieceIndex = message.ParseHave();
        _pieceSelection.PiecePicker.IncrementAvailability(pieceIndex);

        // Update cached bitfield
        var endpointKey = peer.EndpointString ?? "";
        if (endpointKey.Length > 0 && _requestDispatcher.PeerBitfieldCache.TryGetValue(endpointKey, out var cachedBf))
        {
            int byteIdx = pieceIndex / 8;
            int bitIdx = pieceIndex % 8;
            if (byteIdx < cachedBf.Length)
                cachedBf[byteIdx] |= (byte)(0x80 >> bitIdx);
        }

        // Invalidate the cached Bitfield wrapper so it is recreated with the
        // updated PeerBitfield on next dispatch.
        _requestDispatcher.InvalidateCachedBitfield(peer);

        // Check if peer just became a seed
        if (!peer.IsSeed && peer is PeerConnection pc)
        {
            pc.CheckIfSeed(_torrentInfo.PieceCount);
            if (pc.IsSeed) Interlocked.Increment(ref _connectedSeedCount);
        }

        if (!_pieceSelection.LocalBitfield.HasPiece(pieceIndex))
        {
            if (!peer.IsInterested)
            {
                _ = peer.SetInterestedAsync(true);
            }
            if (_peerAvailableSignal.CurrentCount == 0)
            {
                try { _peerAvailableSignal.Release(); }
                catch (SemaphoreFullException) { /* Already signaled */ }
            }
        }

        return Task.CompletedTask;
    }

    public Task HandleBitfieldAsync(IPeerConnection peer, PeerMessage message)
    {
        var bitfield = message.Payload;
        _pieceSelection.PiecePicker.ApplyBitfield(bitfield, _torrentInfo.PieceCount, delta: 1);

        var endpointKey = peer.EndpointString ?? "";
        if (endpointKey.Length > 0 && bitfield != null)
            _requestDispatcher.PeerBitfieldCache[endpointKey] = (byte[])bitfield.Clone();

        // Invalidate the cached Bitfield wrapper so it is recreated on next dispatch.
        _requestDispatcher.InvalidateCachedBitfield(peer);

        if (peer is PeerConnection pc)
        {
            bool wasSeed = pc.IsSeed;
            pc.CheckIfSeed(_torrentInfo.PieceCount);
            if (!wasSeed && pc.IsSeed) Interlocked.Increment(ref _connectedSeedCount);
        }

        bool needsFromPeer;
        if (_pieceSelection.WantedBitfield != null)
        {
            needsFromPeer = _pieceSelection.IsInterestedInPeer(peer.PeerBitfield);
        }
        else
        {
            needsFromPeer = false;
            for (int i = 0; i < _torrentInfo.PieceCount; i++)
            {
                if (!_pieceSelection.LocalBitfield.HasPiece(i) && _pieceSelection.PeerHasPiece(peer, i))
                {
                    needsFromPeer = true;
                    break;
                }
            }
        }

        if (needsFromPeer)
        {
            _ = peer.SetInterestedAsync(true);
        }

        return Task.CompletedTask;
    }

    public Task HandleHaveAllAsync(IPeerConnection peer, PeerMessage message)
    {
        int pieceCount = _torrentInfo.PieceCount;
        int byteCount = (pieceCount + 7) / 8;
        var bitfield = new byte[byteCount];

        for (int i = 0; i < byteCount - 1; i++)
            bitfield[i] = 0xFF;

        int remainingBits = pieceCount % 8;
        if (remainingBits == 0)
            bitfield[byteCount - 1] = 0xFF;
        else
            bitfield[byteCount - 1] = (byte)(0xFF << (8 - remainingBits));

        peer.PeerBitfield = bitfield;

        return HandleBitfieldAsync(peer, PeerMessage.CreateBitfield(bitfield));
    }

    public Task HandleHaveNoneAsync(IPeerConnection peer, PeerMessage message)
    {
        int byteCount = (_torrentInfo.PieceCount + 7) / 8;
        peer.PeerBitfield = new byte[byteCount];
        return Task.CompletedTask;
    }

    public Task HandleChokeAsync(IPeerConnection peer, PeerMessage message)
    {
        LogPeerChokedUs(_logger, peer.PeerInfo.EndPoint);

        var pendingFromPeer = new List<BlockRequest>();
        foreach (var kvp in _requestDispatcher.PendingBlocks)
        {
            if (kvp.Value.Peer == peer)
                pendingFromPeer.Add(kvp.Key);
        }

        foreach (var request in pendingFromPeer)
        {
            if (_requestDispatcher.PendingBlocks.TryRemove(request, out _))
            {
                if (_inProgressPieces.TryGetValue(request.PieceIndex, out var progress))
                {
                    progress.MarkBlockNotRequested(request.Begin);
                }
            }
        }

        if (pendingFromPeer.Count > 0)
        {
            if (_requestDispatcher.PendingCountByPeer.TryGetValue(peer, out var chokeBox))
                Interlocked.Exchange(ref chokeBox.Value, 0);

            LogChokedRequestsCancelled(_logger, pendingFromPeer.Count, peer.PeerInfo.EndPoint);
        }

        return Task.CompletedTask;
    }

    public Task HandleUnchokeAsync(IPeerConnection peer, PeerMessage message)
    {
        LogPeerUnchokedUs(_logger, peer.PeerInfo.EndPoint);
        SignalPeerAvailable();
        return Task.CompletedTask;
    }

    public void SignalPeerAvailable()
    {
        if (_peerAvailableSignal.CurrentCount == 0)
        {
            try { _peerAvailableSignal.Release(); }
            catch (SemaphoreFullException) { /* Already signaled */ }
        }
    }

    public Task HandleSuggestPieceAsync(IPeerConnection peer, PeerMessage message)
    {
        int pieceIndex = message.ParseSuggestPiece();
        LogPeerSuggestsPiece(_logger, peer.PeerInfo.EndPoint, pieceIndex);
        return Task.CompletedTask;
    }

    public Task HandleRejectRequestAsync(IPeerConnection peer, PeerMessage message)
    {
        var (pieceIndex, begin, length) = message.ParseRejectRequest();
        var request = new BlockRequest(pieceIndex, begin, length);
        _requestDispatcher.PendingBlocks.TryRemove(request, out _);

        if (_inProgressPieces.TryGetValue(pieceIndex, out var progress))
        {
            progress.MarkBlockNotRequested(begin);
        }

        LogPeerRejectedRequest(_logger, peer.PeerInfo.EndPoint, pieceIndex);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when a peer sends DONTHAVE (BEP 54) — updates bitfield,
    /// decrements availability, and cancels requests for non-Fast peers.
    /// </summary>
    public void OnPeerLostPiece(IPeerConnection peer, int pieceIndex)
    {
        // 1. Clear bit in peer's bitfield (MSB-first: piece 0 = bit 7 of byte 0)
        if (peer.PeerBitfield != null)
        {
            int byteIndex = pieceIndex / 8;
            int bitIndex = pieceIndex % 8;
            if (byteIndex < peer.PeerBitfield.Length)
                peer.PeerBitfield[byteIndex] &= (byte)~(0x80 >> bitIndex);
        }

        // 2. Decrement piece availability
        _pieceSelection.PiecePicker.DecrementAvailability(pieceIndex);

        // 3. For non-Fast peers, cancel outstanding requests for the lost piece
        if (!peer.PeerSupportsFastExtension)
        {
            CancelRequestsForPieceFromPeer(peer, pieceIndex);
        }

        LogPeerLostPiece(_logger, peer.PeerInfo?.EndPoint, pieceIndex);
    }

    private void CancelRequestsForPieceFromPeer(IPeerConnection peer, int pieceIndex)
    {
        var toCancel = new List<BlockRequest>();
        foreach (var kvp in _requestDispatcher.PendingBlocks)
        {
            if (kvp.Value.Peer == peer && kvp.Key.PieceIndex == pieceIndex)
                toCancel.Add(kvp.Key);
        }

        foreach (var block in toCancel)
        {
            if (_requestDispatcher.PendingBlocks.TryRemove(block, out _))
            {
                if (_inProgressPieces.TryGetValue(block.PieceIndex, out var progress))
                    progress.MarkBlockNotRequested(block.Begin);

                if (_requestDispatcher.PendingCountByPeer.TryGetValue(peer, out var countBox))
                {
                    var val = Interlocked.Decrement(ref countBox.Value);
                    if (val < 0) Interlocked.Exchange(ref countBox.Value, 0);
                }
            }
        }

        if (toCancel.Count > 0)
        {
            LogCancelledDontHaveRequests(_logger, toCancel.Count, pieceIndex, peer.PeerInfo?.EndPoint);
            SignalPeerAvailable();
        }
    }

    #endregion

    // --- Download Loop ---

    private async Task DownloadLoopAsync(CancellationToken cancellationToken)
    {
        int currentDelayMs = 10;
        const int MinDelayMs = 5;
        const int MaxDelayMs = 100;
        const int IdleDelayMs = 500;

        var lastTimeoutCheck = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested && !IsWantedComplete && _isRunning)
        {
            try
            {
                // Cancel non-critical requests when streaming mode activates
                if (_cancelNonCriticalPending)
                {
                    _cancelNonCriticalPending = false;
                    await CancelNonCriticalRequestsAsync().ConfigureAwait(false);
                }

                _availablePeers.Clear();
                foreach (var p in _peerManager.ConnectedPeers)
                {
                    if (p.IsConnected && !p.IsChoked)
                        _availablePeers.Add(p);
                }

                // Append web seeds
                if (_webSeedManager != null)
                {
                    foreach (var ws in _webSeedManager.ActiveConnections)
                    {
                        if (ws.IsConnected)
                        {
                            _availablePeers.Add(ws);
                            _requestDispatcher.SlowStartExited.TryAdd(ws, true);
                        }
                    }
                }

                if (_availablePeers.Count == 0)
                {
                    await _peerAvailableSignal.WaitAsync(IdleDelayMs, cancellationToken).ConfigureAwait(false);
                    currentDelayMs = MinDelayMs;
                    continue;
                }

                int requestsSent = 0;

                // Snapshot in-progress pieces once per tick (reuse buffer to avoid allocation)
                _inProgressSnapshotBuffer.Clear();
                foreach (var kvp in _inProgressPieces)
                    _inProgressSnapshotBuffer.Add(kvp);

                var peerCount = _availablePeers.Count;
                if (_peerTaskBuffer.Length < peerCount)
                    _peerTaskBuffer = new Task<int>[Math.Max(peerCount, 16)];

                for (int i = 0; i < peerCount; i++)
                    _peerTaskBuffer[i] = _requestDispatcher.RequestBlocksFromPeerWithCountAsync(
                        _availablePeers[i], _pieceSelection, _inProgressPieces, cancellationToken, _inProgressSnapshotBuffer);

                var results = await Task.WhenAll(new ArraySegment<Task<int>>(_peerTaskBuffer, 0, peerCount)).ConfigureAwait(false);
                for (int i = 0; i < results.Length; i++)
                    requestsSent += results[i];

                Array.Clear(_peerTaskBuffer, 0, peerCount);

                if (requestsSent > 0)
                    _pipelineTick?.ReportDispatch();

                // Flush all batched outgoing messages
                await _writeBatcher.FlushAllAsync(cancellationToken).ConfigureAwait(false);

                // Adaptive delay
                if (requestsSent > 0)
                    currentDelayMs = MinDelayMs;
                else
                    currentDelayMs = Math.Min(currentDelayMs * 2, MaxDelayMs);

                // Check timeouts and snubbed peers every ~500ms
                var now = DateTime.UtcNow;
                if ((now - lastTimeoutCheck).TotalMilliseconds >= 500)
                {
                    lastTimeoutCheck = now;

                    bool endgame = IsEndgameMode;
                    NotifyProberEndgameTransition(endgame);

                    await _requestDispatcher.CheckTimeoutsAsync(_inProgressPieces, endgame, cancellationToken).ConfigureAwait(false);
                    _requestDispatcher.CheckSnubbedPeers(now, _lastDataReceived, _inProgressPieces, _snubThresholdSeconds);
                    _pieceCompletion.RepairOrphanedBlocks(_requestDispatcher.PendingBlocks, _pieceSelection, endgame, SignalPeerAvailable);
                    _pieceCompletion.DiagnoseStuckPieces(_pieceSelection, _requestDispatcher.PendingBlocks, _peerManager, _diskWriteCache);

                    // Auto-sequential detection
                    if (_pieceSelection.ShouldCheckAutoSequential() && (_behaviorMonitor?.CurrentValue.AutoSequentialInSeederSwarm ?? true))
                        _pieceSelection.UpdateAutoSequential(_peerManager, _behaviorMonitor);

                    // Retry web seeds in backoff state
                    if (_webSeedManager != null)
                        await _webSeedManager.TryRetryBackoffSeedsAsync(cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(currentDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in download loop");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }

        if (IsWantedComplete)
        {
            _logger.LogDebug("Download completed!");
            DownloadCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task CancelNonCriticalRequestsAsync()
    {
        if (_streamingManager == null) return;

        var critical = _streamingManager.GetTimeCriticalPieces(
            idx => _pieceSelection.PiecePicker.IsPieceCompleted(idx));

        var criticalSet = new HashSet<int>(critical.Count);
        foreach (var p in critical)
            criticalSet.Add(p.PieceIndex);

        var toCancel = new List<(IPeerConnection peer, BlockRequest block)>();
        foreach (var kvp in _requestDispatcher.PendingBlocks)
        {
            if (!criticalSet.Contains(kvp.Key.PieceIndex))
                toCancel.Add((kvp.Value.Peer, kvp.Key));
        }

        foreach (var (peer, block) in toCancel)
        {
            if (_requestDispatcher.PendingBlocks.TryRemove(block, out var pending))
            {
                if (_requestDispatcher.PendingCountByPeer.TryGetValue(pending.Peer, out var cancelBox))
                {
                    var cancelVal = Interlocked.Decrement(ref cancelBox.Value);
                    if (cancelVal < 0) Interlocked.Exchange(ref cancelBox.Value, 0);
                }

                try
                {
                    await peer.CancelBlockAsync(
                        block.PieceIndex, block.Begin, block.Length).ConfigureAwait(false);
                }
                catch { /* Best-effort */ }
            }
        }

        LogCancelledNonCriticalRequests(_logger, toCancel.Count);
    }

    // --- Progress Reporting ---

    private void ReportProgress()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastProgressReportTicks);
        if (now - last < ProgressReportIntervalTicks)
            return;

        if (Interlocked.CompareExchange(ref _lastProgressReportTicks, now, last) != last)
            return;

        if (ProgressChanged != null)
        {
            var args = new DownloadProgressEventArgs(
                PiecesCompleted,
                TotalPieces,
                BytesDownloaded,
                BytesInProgress,
                _torrentInfo.TotalSize,
                DownloadRate,
                _requestDispatcher.PendingRequestCount,
                _inProgressPieces.Count,
                _statisticsTracker.FailedBytes);

            ProgressChanged.Invoke(this, args);
        }
    }

    // --- Dispose ---

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_streamingManager != null)
            _streamingManager.PieceAvailable -= OnTimeCriticalPieceAvailable;

        _pipelineTick?.Dispose();
        _peerManager.PeerDisconnected -= OnPeerDisconnected;
        _stopCts.Cancel();
        _stopCts.Dispose();
        _peerAvailableSignal.Dispose();
        _writeBatcher.Dispose();
        _diskWriteCache.DisposeAll();

        _logger.LogDebug("DownloadCoordinator disposed");
    }

    // --- Source-generated logging (zero allocation when level disabled) ---

    [LoggerMessage(Level = LogLevel.Trace, Message = "Received block: piece {PieceIndex}, offset {Offset}, length {Length}")]
    private static partial void LogBlockReceived(ILogger logger, int pieceIndex, int offset, int length);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Discarding duplicate endgame block: piece {PieceIndex}, offset {Offset}")]
    private static partial void LogDuplicateEndgameBlock(ILogger logger, int pieceIndex, int offset);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Duplicate block for piece {PieceIndex} offset {Offset} — already received")]
    private static partial void LogDuplicateBlock(ILogger logger, int pieceIndex, int offset);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Peer {Peer} choked us")]
    private static partial void LogPeerChokedUs(ILogger logger, object peer);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cancelled {Count} pending requests due to choke from {Peer}")]
    private static partial void LogChokedRequestsCancelled(ILogger logger, int count, object peer);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Peer {Peer} unchoked us")]
    private static partial void LogPeerUnchokedUs(ILogger logger, object peer);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Peer {Peer} suggests piece {PieceIndex}")]
    private static partial void LogPeerSuggestsPiece(ILogger logger, object peer, int pieceIndex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Peer {Peer} rejected request for piece {PieceIndex}")]
    private static partial void LogPeerRejectedRequest(ILogger logger, object peer, int pieceIndex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Peer {Peer} lost piece {PieceIndex} (DONTHAVE)")]
    private static partial void LogPeerLostPiece(ILogger logger, object peer, int pieceIndex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cancelled {Count} pending requests for piece {PieceIndex} from peer {Peer} (DONTHAVE)")]
    private static partial void LogCancelledDontHaveRequests(ILogger logger, int count, int pieceIndex, object peer);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Time-critical piece {PieceIndex} now available for streaming")]
    private static partial void LogTimeCriticalPieceAvailable(ILogger logger, int pieceIndex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cancelled {Count} non-critical block requests")]
    private static partial void LogCancelledNonCriticalRequests(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Peer {Peer} disconnected, released {Count} pending blocks")]
    private static partial void LogPeerDisconnectedReleasedBlocks(ILogger logger, object peer, int count);
}
