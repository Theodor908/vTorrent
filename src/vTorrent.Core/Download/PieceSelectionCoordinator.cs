using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.PeerCommunication;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Utilities;
using vTorrent.Core.Engine;
using vTorrent.Core.PieceIO;
using vTorrent.Core.Session;
using vTorrent.Core.Streaming;

namespace vTorrent.Core.Download;

/// <summary>
/// Manages piece selection strategy: rarest-first, sequential, streaming deadlines,
/// file priorities, first/last piece priority, and wanted bitfield computation.
/// Extracted from DownloadCoordinator (Phase 4 refactor).
/// </summary>
internal sealed class PieceSelectionCoordinator
{
    private readonly ILogger _logger;
    private readonly TorrentInfo _torrentInfo;
    private readonly Bitfield _localBitfield;
    private readonly BucketPiecePicker _piecePicker;
    private readonly IEndgameStrategy _endgameStrategy;
    private readonly PeerSettings _settings;
    private readonly IOptionsMonitor<BehaviorSettings>? _behaviorMonitor;
    private readonly IOptionsMonitor<DiskSettings>? _diskMonitor;
    private readonly int _blockSize;
    private readonly object _pieceLock = new();

    // Piece state
    private byte[]? _wantedBitfield;
    private bool _sequentialMode;
    private bool _autoSequentialMode;
    private int _autoSequentialCheckCounter;
    private bool _firstLastPiecePriority;
    private HashSet<int>? _firstLastPieces;
    private FileProgressTracker? _fileProgressTracker;
    private IStreamingManager? _streamingManager;
    private IPieceManager? _pieceManager; // for sequential access hint

    // O(1) IsWantedComplete cache
    private int _wantedPieceCount;
    private int _wantedHaveCount;

    public PieceSelectionCoordinator(
        TorrentInfo torrentInfo,
        Bitfield localBitfield,
        PeerSettings settings,
        IEndgameStrategy endgameStrategy,
        IPieceManager pieceManager,
        ILogger logger,
        IOptionsMonitor<BehaviorSettings>? behaviorMonitor = null,
        IOptionsMonitor<DiskSettings>? diskMonitor = null)
    {
        _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(torrentInfo));
        _localBitfield = localBitfield ?? throw new ArgumentNullException(nameof(localBitfield));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _endgameStrategy = endgameStrategy ?? throw new ArgumentNullException(nameof(endgameStrategy));
        _behaviorMonitor = behaviorMonitor;
        _diskMonitor = diskMonitor;
        _pieceManager = pieceManager;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _blockSize = PeerConstants.BlockSize;

        // Initialize piece picker
        _piecePicker = new BucketPiecePicker(torrentInfo.PieceCount);
        _piecePicker.SetPrioritizePartialPieces(settings.PrioritizePartialPieces);

        for (int i = 0; i < torrentInfo.PieceCount; i++)
        {
            if (localBitfield.HasPiece(i))
                _piecePicker.MarkCompleted(i);
        }
    }

    // --- Properties ---

    public bool IsComplete => _localBitfield.IsComplete;

    public bool IsWantedComplete
    {
        get
        {
            if (_fileProgressTracker == null)
                return _localBitfield.IsComplete;
            return _wantedHaveCount >= _wantedPieceCount;
        }
    }

    /// <summary>
    /// Whether file priorities are active (selective download).
    /// </summary>
    public bool HasFilePriorities => _fileProgressTracker != null;

    /// <summary>
    /// Check if all wanted pieces are present in the given bitfield.
    /// Used by post-verification evaluation where _wantedHaveCount may not reflect
    /// pieces set by background verification (which bypasses IncrementWantedHaveCount).
    /// </summary>
    public bool AreWantedPiecesComplete(Bitfield bitfield)
    {
        if (_fileProgressTracker == null)
            return bitfield.IsComplete;

        for (int i = 0; i < _torrentInfo.PieceCount; i++)
        {
            if (_fileProgressTracker.IsPieceWanted(i) && !bitfield.HasPiece(i))
                return false;
        }
        return true;
    }

    public int PiecesCompleted => _localBitfield.CompletePieces;
    public int TotalPieces => _torrentInfo.PieceCount;
    public double Progress => _localBitfield.Progress;
    public bool IsSequentialMode => _sequentialMode || _autoSequentialMode;
    public bool IsStreaming => _streamingManager?.HasDeadlines ?? false;
    public BucketPiecePicker PiecePicker => _piecePicker;
    public Bitfield LocalBitfield => _localBitfield;
    public byte[]? WantedBitfield => _wantedBitfield;
    public FileProgressTracker? FileProgressTracker => _fileProgressTracker;
    public object PieceLock => _pieceLock;

    // --- Public API ---

    public void SetSequentialMode(bool enabled)
    {
        if (_sequentialMode != enabled)
        {
            _sequentialMode = enabled;
            _pieceManager?.SetSequentialAccessHint(enabled || _autoSequentialMode);
            _logger.LogDebug("Sequential download mode {State}", enabled ? "enabled" : "disabled");
        }
    }

    public void SetAutoSequentialMode(bool enabled)
    {
        if (_autoSequentialMode != enabled)
        {
            _autoSequentialMode = enabled;
            _pieceManager?.SetSequentialAccessHint(enabled || _sequentialMode);
            _logger.LogDebug("Auto-sequential mode (seeder swarm) {State}", enabled ? "enabled" : "disabled");
        }
    }

    public void SetFileProgressTracker(FileProgressTracker tracker)
    {
        lock (_pieceLock)
        {
            _fileProgressTracker = tracker;
            RebuildFirstLastPieceSet();
            RecomputeWantedCounters();
        }
    }

    public void SetFirstLastPiecePriority(bool enabled)
    {
        lock (_pieceLock)
        {
            _firstLastPiecePriority = enabled;
            RebuildFirstLastPieceSet();
        }
    }

    public void SetFilePriorities(FilePriority[] priorities)
    {
        lock (_pieceLock)
        {
            _fileProgressTracker?.SetFilePriorities(priorities);
            RebuildFirstLastPieceSet();
            RecomputeWantedCounters();
        }
    }

    public void SetPrioritizePartialPieces(bool value)
    {
        _piecePicker.SetPrioritizePartialPieces(value);
    }

    public void SetStrictEndgameMode(bool strict)
    {
        (_endgameStrategy as EndgameManager)?.SetStrictEndgameMode(strict);
    }

    public void SetStreamingManager(IStreamingManager streamingManager)
    {
        _streamingManager = streamingManager;
    }

    public void SetPieceDeadline(int pieceIndex, int deadlineMs, bool alertWhenAvailable = false)
    {
        if (_streamingManager == null) return;

        if (_piecePicker.IsPieceCompleted(pieceIndex))
        {
            if (alertWhenAvailable)
                _streamingManager.OnPieceCompleted(pieceIndex);
            return;
        }

        _streamingManager.SetPieceDeadline(pieceIndex, deadlineMs, alertWhenAvailable);
    }

    /// <summary>
    /// Sets a piece deadline and returns true if streaming mode was just activated
    /// (i.e., this was the first deadline that caused cancel-non-critical).
    /// </summary>
    public bool SetPieceDeadlineAndCheckActivation(int pieceIndex, int deadlineMs, bool alertWhenAvailable = false)
    {
        if (_streamingManager == null) return false;

        if (_piecePicker.IsPieceCompleted(pieceIndex))
        {
            if (alertWhenAvailable)
                _streamingManager.OnPieceCompleted(pieceIndex);
            return false;
        }

        return _streamingManager.SetPieceDeadline(pieceIndex, deadlineMs, alertWhenAvailable);
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

    /// <summary>
    /// Initialize the wanted bitfield. Called when download starts.
    /// </summary>
    public void InitializeWantedBitfield()
    {
        _wantedBitfield = ComputeWantedBitfield();
    }

    // --- Piece Selection ---

    /// <summary>
    /// Selects up to maxBlocks from available pieces for this peer under a single lock.
    /// Lock protects compound select-and-start operation:
    /// PickPiece + MarkInProgress + _inProgressPieces insertion must be atomic
    /// to prevent two peers from starting the same piece.
    /// (Picker's internal lock handles its own array consistency.)
    /// </summary>
    public List<BlockRequest> SelectBlockBatch(
        IPeerConnection peer, int maxBlocks,
        ConcurrentDictionary<int, PieceBlockTracker> inProgressPieces,
        ConcurrentDictionary<BlockRequest, PendingBlock> pendingBlocks,
        IReadOnlyList<KeyValuePair<int, PieceBlockTracker>>? inProgressSnapshot = null,
        double peerDownloadRate = 0.0)
    {
        if (peer.PeerBitfield == null || maxBlocks <= 0)
            return new List<BlockRequest>(0);

        // InitialPickerThreshold: use sequential order until we have enough pieces to
        // make rarity estimates reliable. libtorrent: piece_picker::pick_pieces()
        var behavior = _behaviorMonitor?.CurrentValue;
        int initialPickerThreshold = behavior?.InitialPickerThreshold ?? 4;
        bool belowInitialThreshold = _localBitfield.CompletePieces < initialPickerThreshold;

        // WholePiecesThreshold: assign all blocks of a piece to fast peers to reduce
        // fragmentation. libtorrent: peer_connection::request_a_block().
        int wholePiecesThreshold = behavior?.WholePiecesThreshold ?? 20;
        long pieceLength = _torrentInfo.PieceLength;
        double pieceDownloadTime = pieceLength / Math.Max(peerDownloadRate, 1.0);
        bool wholePieceMode = peerDownloadRate > 0 && pieceDownloadTime < wholePiecesThreshold;
        if (wholePieceMode)
        {
            int blocksPerPiece = (int)Math.Ceiling((double)pieceLength / _blockSize);
            maxBlocks = Math.Max(maxBlocks, blocksPerPiece);
        }

        var batch = new List<BlockRequest>(Math.Min(maxBlocks, 128));

        // Priority 0: Deadline (streaming) pieces — highest priority, inside _pieceLock
        if (_streamingManager != null && _streamingManager.HasDeadlines)
        {
            lock (_pieceLock)
            {
                var criticalPieces = _streamingManager.GetTimeCriticalPieces(
                    idx => _piecePicker.IsPieceCompleted(idx));

                foreach (var critical in criticalPieces)
                {
                    if (batch.Count >= maxBlocks) break;
                    int pieceIdx = critical.PieceIndex;
                    if (!PeerHasPiece(peer, pieceIdx)) continue;

                    if (!inProgressPieces.TryGetValue(pieceIdx, out var progress))
                    {
                        var pieceSize = GetPieceSize(pieceIdx);
                        progress = new PieceBlockTracker(pieceIdx, pieceSize, _blockSize);
                        inProgressPieces.TryAdd(pieceIdx, progress);
                        _piecePicker.MarkInProgress(pieceIdx);
                    }

                    while (batch.Count < maxBlocks)
                    {
                        var block = progress.GetNextBlock(peer.EndpointString ?? "");
                        if (block == null) break;
                        batch.Add(block.Value);
                    }

                    if (batch.Count > 0)
                        _streamingManager.IncrementPeerCount(pieceIdx);
                }

                if (batch.Count >= maxBlocks)
                    return batch;
            }
        }

        // Pieces started in this call — invisible to the stale inProgressSnapshot
        // but visible here so the while loop can fill from them on subsequent iterations.
        List<KeyValuePair<int, PieceBlockTracker>> newlyStartedPieces = null;

        string peerEndpoint = peer.EndpointString ?? "";

        var piecesToIterate = inProgressSnapshot ?? inProgressPieces.ToArray();

        while (batch.Count < maxBlocks)
        {
            // Priority 1: Complete in-progress pieces (unique blocks)
            // Check newly started pieces first (most likely to have free blocks,
            // and invisible to the stale snapshot).
            BlockRequest? block = null;

            if (newlyStartedPieces != null)
            {
                foreach (var kvp in newlyStartedPieces)
                {
                    if (!PeerHasPiece(peer, kvp.Key)) continue;
                    block = kvp.Value.GetNextBlock(peerEndpoint);
                    if (block != null) break;
                }
            }

            if (block == null)
            {
                foreach (var (pieceIndex, progress) in piecesToIterate)
                {
                    if (!PeerHasPiece(peer, pieceIndex))
                        continue;

                    if (_fileProgressTracker != null && !_fileProgressTracker.IsPieceWanted(pieceIndex))
                        continue;

                    block = progress.GetNextBlock(peerEndpoint);

                    if (block != null)
                        break;
                }
            }

            // Priority 1.5 & 2: Pick NEW piece — lock protects the compound
            // PickPiece + MarkInProgress to prevent two peers from starting
            // the same piece. Tracker creation and block selection happen
            // outside the lock since the tracker is newly created (no other
            // thread has a reference) and inProgressPieces is ConcurrentDictionary.
            if (block == null)
            {
                PieceBlockTracker newPieceBlockTracker = null;

                // Read settings outside lock (volatile/IOptionsMonitor reads are thread-safe)
                bool useReverse = peer.IsSnubbed;
                var diskSettings = _diskMonitor?.CurrentValue;
                bool extentAffinity = diskSettings?.PieceExtentAffinity ?? false;
                int extentSize = diskSettings?.PieceExtentSize ?? 4_194_304;
                int extentPieceLength = extentAffinity && !useReverse ? (int)Math.Max(1, _torrentInfo.PieceLength) : 0;
                bool forceSequential = belowInitialThreshold
                    || IsSequentialMode
                    || peer is WebSeedConnection
                    || peer is HttpSeedConnection;

                int? pickedPiece = null;

                lock (_pieceLock)
                {
                    // Priority 1.5: First/last piece priority
                    if (_firstLastPieces != null)
                    {
                        pickedPiece = _piecePicker.PickPiece(
                            i => _firstLastPieces.Contains(i) && PeerHasPiece(peer, i) && !inProgressPieces.ContainsKey(i),
                            sequential: false);
                    }

                    // Priority 2: New piece via BucketPiecePicker
                    // libtorrent pattern: snubbed peers pick in reverse order
                    // (highest availability first) to concentrate them on common pieces
                    // and prevent blocking rare piece completion for fast peers.
                    if (pickedPiece == null)
                    {
                        pickedPiece = useReverse
                            ? _piecePicker.PickPieceReverse(
                                i => PeerHasPiece(peer, i) && !inProgressPieces.ContainsKey(i)
                                     && (_fileProgressTracker == null || _fileProgressTracker.IsPieceWanted(i)))
                            : _piecePicker.PickPiece(
                                i => PeerHasPiece(peer, i) && !inProgressPieces.ContainsKey(i)
                                     && (_fileProgressTracker == null || _fileProgressTracker.IsPieceWanted(i)),
                                sequential: forceSequential,
                                extentPieceLength: extentPieceLength,
                                extentSize: extentAffinity && !useReverse ? extentSize : 0);
                    }

                    if (pickedPiece != null)
                    {
                        _piecePicker.MarkInProgress(pickedPiece.Value);
                    }
                }

                // Outside lock: create tracker, get block, insert into ConcurrentDictionary.
                // Safe because the tracker is brand new (no other thread references it)
                // and MarkInProgress already prevents another thread from picking the same piece.
                if (pickedPiece != null)
                {
                    var pieceSize = GetPieceSize(pickedPiece.Value);
                    newPieceBlockTracker = new PieceBlockTracker(pickedPiece.Value, pieceSize, _blockSize);
                    inProgressPieces[pickedPiece.Value] = newPieceBlockTracker;
                    block = newPieceBlockTracker.GetNextBlock(peerEndpoint);
                }

                // libtorrent prefer_contiguous_blocks: after starting a new piece,
                // fill batch slots from it directly (piece affinity). Then track it
                // in newlyStartedPieces so subsequent iterations can fill from it too.
                if (block != null && newPieceBlockTracker != null)
                {
                    batch.Add(block.Value);

                    while (batch.Count < maxBlocks)
                    {
                        var nextBlock = newPieceBlockTracker.GetNextBlock(peerEndpoint);
                        if (nextBlock == null) break;
                        batch.Add(nextBlock.Value);
                    }

                    // Track this piece so Priority 1 can see it on next iteration
                    newlyStartedPieces ??= new List<KeyValuePair<int, PieceBlockTracker>>(4);
                    newlyStartedPieces.Add(new KeyValuePair<int, PieceBlockTracker>(
                        newPieceBlockTracker.PieceIndex, newPieceBlockTracker));

                    continue; // Keep filling — don't break with empty slots
                }
            }

            if (block != null)
            {
                batch.Add(block.Value);
                continue;
            }

            // Priority 3: Endgame — request exactly 1 duplicate block (libtorrent strict_end_game_mode).
            // Pipelining disabled — only activate when no unique blocks were found.
            if (batch.Count == 0)
            {
                var duplicates = _endgameStrategy.PickDuplicateBlocks(
                    peer, inProgressPieces, pendingBlocks,
                    (p, i) => PeerHasPiece(p, i),
                    1);

                batch.AddRange(duplicates);
            }

            break;
        }

        return batch;
    }

    // --- Utility Methods ---

    public bool PeerHasPiece(IPeerConnection peer, int pieceIndex)
    {
        if (peer.PeerBitfield == null)
            return false;

        int byteIndex = pieceIndex / 8;
        int bitIndex = 7 - (pieceIndex % 8);

        if (byteIndex >= peer.PeerBitfield.Length)
            return false;

        return (peer.PeerBitfield[byteIndex] & (1 << bitIndex)) != 0;
    }

    public long GetPieceSize(int pieceIndex)
    {
        return TorrentUtilities.GetPieceSize(_torrentInfo, pieceIndex);
    }

    public bool IsInterestedInPeer(byte[]? peerBitfield)
    {
        if (peerBitfield == null || _wantedBitfield == null) return false;

        int len = Math.Min(peerBitfield.Length, _wantedBitfield.Length);

        for (int i = 0; i < len; i++)
        {
            if ((peerBitfield[i] & _wantedBitfield[i]) != 0)
                return true;
        }

        return false;
    }

    public byte[] ComputeWantedBitfield()
    {
        int byteCount = (_torrentInfo.PieceCount + 7) / 8;
        var wanted = new byte[byteCount];

        for (int i = 0; i < _torrentInfo.PieceCount; i++)
        {
            bool isWanted = _fileProgressTracker == null || _fileProgressTracker.IsPieceWanted(i);
            bool alreadyHave = _localBitfield.HasPiece(i);

            if (isWanted && !alreadyHave)
                wanted[i / 8] |= (byte)(0x80 >> (i % 8)); // MSB-first
        }

        return wanted;
    }

    public void RecomputeWantedCounters()
    {
        int wantedTotal = 0;
        int wantedHave = 0;

        if (_fileProgressTracker != null)
        {
            for (int i = 0; i < _torrentInfo.PieceCount; i++)
            {
                if (_fileProgressTracker.IsPieceWanted(i))
                {
                    wantedTotal++;
                    if (_localBitfield.HasPiece(i))
                        wantedHave++;
                }
            }
        }

        Interlocked.Exchange(ref _wantedPieceCount, wantedTotal);
        Interlocked.Exchange(ref _wantedHaveCount, wantedHave);
    }

    public void UpdateAutoSequential(IPeerManager peerManager, IOptionsMonitor<BehaviorSettings>? behaviorMonitor = null)
    {
        if (!(_behaviorMonitor?.CurrentValue.AutoSequentialInSeederSwarm ?? true))
            return;

        int seeds = 0, downloaders = 0;
        foreach (var peer in peerManager.ConnectedPeers)
        {
            if (peer.IsSeed) seeds++;
            else downloaders++;
        }

        bool shouldEnable = AutoSequentialDetector.ShouldEnable(seeds, downloaders);
        SetAutoSequentialMode(shouldEnable);
    }

    /// <summary>
    /// Increment the auto-sequential check counter and return true every 10th call.
    /// </summary>
    public bool ShouldCheckAutoSequential()
    {
        return _autoSequentialCheckCounter++ % 10 == 0;
    }

    /// <summary>
    /// Increment wanted-have counter when a wanted piece is completed. Thread-safe.
    /// </summary>
    public void IncrementWantedHaveCount()
    {
        Interlocked.Increment(ref _wantedHaveCount);
    }

    /// <summary>
    /// Update wanted bitfield to clear a completed piece.
    /// </summary>
    public void ClearWantedBit(int pieceIndex)
    {
        if (_wantedBitfield != null)
        {
            int byteIdx = pieceIndex / 8;
            int bitIdx = pieceIndex % 8;

            if (byteIdx < _wantedBitfield.Length)
                _wantedBitfield[byteIdx] &= (byte)~(0x80 >> bitIdx);
        }
    }

    /// <summary>
    /// BEP 52 helpers: returns true if the torrent requires hash gate (v2 or hybrid).
    /// V1 torrents have all piece hashes upfront — no gate needed.
    /// </summary>
    public static bool RequiresHashGate(TorrentInfo info)
    {
        return info.Version is TorrentVersion.V2 or TorrentVersion.Hybrid;
    }

    // --- Private Helpers ---

    private void RebuildFirstLastPieceSet()
    {
        if (!_firstLastPiecePriority || _fileProgressTracker == null)
        {
            _firstLastPieces = null;
            return;
        }

        _firstLastPieces = new HashSet<int>();
        foreach (var fp in _fileProgressTracker.Files)
        {
            if (!fp.IsWanted) continue;
            _firstLastPieces.Add(fp.FirstPiece);
            _firstLastPieces.Add(fp.LastPiece);
        }
    }
}
