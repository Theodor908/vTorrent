using System;

using System.Collections.Concurrent;

using System.Collections.Generic;

using System.Threading;

using Microsoft.Extensions.Logging;

using vTorrent.Core.PeerCommunication.Models;

using vTorrent.Abstractions.Enums;

using vTorrent.Abstractions.Models;

using vTorrent.Abstractions.Interfaces.Engine;
using vTorrent.Core.Engine;
using vTorrent.Core.Download;

namespace vTorrent.Core.Session;

/// <summary>

/// Unified torrent statistics: thread-safe live tracking + snapshot DTO.

/// Merges the former TorrentStatisticsTracker (atomic counters, rate calculators, per-peer stats)

/// with the former snapshot-only TorrentStatistics (UI/persistence properties).

/// Implements IStatisticsTracker for use by engine internals.

/// </summary>

public class TorrentStatistics : IStatisticsTracker, ITransferAccumulator, IDisposable

{

    // Logger (null for snapshot instances)

    private readonly ILogger? _logger;

    #region Atomic Counters (thread-safe, from former TorrentStatisticsTracker)

    // Total traffic (includes protocol overhead)

    private long _totalDownloaded;

    private long _totalUploaded;

    private long _sessionDownloaded;

    private long _sessionUploaded;

    // Payload only (actual file data)

    private long _payloadDownloaded;

    private long _payloadUploaded;

    // Verified only (hash-verified pieces written to disk)

    private long _verifiedDownloaded;

    // Endgame waste tracking

    private long _endgameWastedBytes;

    private int _endgameDuplicateBlocks;

    // All-time payload counters (atomic for ITransferAccumulator thread safety)

    private long _allTimePayloadDownloaded;

    private long _allTimePayloadUploaded;

    // Verification progress (0.0 to 1.0), only meaningful during Verifying state

    private double _verificationProgress;

    private int _piecesCompleted;

    private int _piecesUploaded;

    private long _failedBytes;

    // Disk I/O statistics (from former DiskStatisticsTracker)

    private long _diskBytesRead;

    private long _diskReadOperations;

    private long _diskBytesWritten;

    private long _diskWriteOperations;

    private int _diskPendingReads;

    private int _diskPendingWrites;

    private long _diskHashOperations;

    private long _diskHashPassed;

    private long _diskHashFailed;

    private long _diskCacheHits;

    private long _diskCacheMisses;

    #endregion

    #region Rate Calculators (null for snapshot instances)

    private readonly SlidingWindowRateCalculator? _downloadRateCalc;

    private readonly SlidingWindowRateCalculator? _uploadRateCalc;

    private readonly SlidingWindowRateCalculator? _payloadDownloadRateCalc;

    private readonly SlidingWindowRateCalculator? _payloadUploadRateCalc;

    private readonly SlidingWindowRateCalculator? _verifiedDownloadRateCalc;

    #endregion

    #region Per-Peer Tracking (null for snapshot instances)

    private readonly ConcurrentDictionary<IPeerConnection, PeerTransferStats>? _peerStats;

    private readonly ITransferAccumulator? _accumulator;

    #endregion

    #region Snapshot Rate Values (used when rate calculators are null)

    private double _snapshotDownloadRate;

    private double _snapshotUploadRate;

    private double _snapshotPayloadDownloadRate;

    private double _snapshotPayloadUploadRate;

    private double _snapshotSmoothedPayloadDownloadRate;

    private double _snapshotVerifiedDownloadRate;

    #endregion

    #region Constructors

    /// <summary>

    /// Creates a live tracking instance with rate calculators and per-peer tracking.

    /// </summary>

    public TorrentStatistics(ILogger logger)

    {

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Use 10-second window for rate calculation to handle bursty P2P network traffic.

        _downloadRateCalc = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));

        _uploadRateCalc = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));

        _payloadDownloadRateCalc = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));

        _payloadUploadRateCalc = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));

        _verifiedDownloadRateCalc = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));

        _peerStats = new ConcurrentDictionary<IPeerConnection, PeerTransferStats>();

    }

    /// <summary>

    /// Creates a live tracking instance that forwards all-time counters to an external accumulator.

    /// Used when the engine's session stats should accumulate into ManagedTorrent's persistent counters.

    /// </summary>

    public TorrentStatistics(ILogger logger, ITransferAccumulator accumulator) : this(logger)

    {

        _accumulator = accumulator ?? throw new ArgumentNullException(nameof(accumulator));

    }

    /// <summary>

    /// Creates a snapshot/DTO instance (no rate calculators, no peer tracking).

    /// Used by CreateSnapshot() and for deserialization.

    /// </summary>

    public TorrentStatistics()

    {

        // Snapshot mode: no rate calculators, no peer tracking

    }

    #endregion

    #region IStatisticsTracker - Transfer Properties

    // Total traffic (includes protocol overhead)

    long IStatisticsTracker.TotalDownloaded => Interlocked.Read(ref _totalDownloaded);

    long IStatisticsTracker.TotalUploaded => Interlocked.Read(ref _totalUploaded);

    /// <summary>

    /// Bytes downloaded this session (total traffic including protocol overhead).

    /// Thread-safe atomic read.

    /// </summary>

    public long SessionDownloaded

    {

        get => Interlocked.Read(ref _sessionDownloaded);

        set => Interlocked.Exchange(ref _sessionDownloaded, value);

    }

    /// <summary>

    /// Bytes uploaded this session (total traffic including protocol overhead).

    /// Thread-safe atomic read.

    /// </summary>

    public long SessionUploaded

    {

        get => Interlocked.Read(ref _sessionUploaded);

        set => Interlocked.Exchange(ref _sessionUploaded, value);

    }

    /// <summary>

    /// Current download rate in bytes/second (total traffic).

    /// Live instance: calculated from sliding window. Snapshot: stored value.

    /// </summary>

    public double DownloadRate

    {

        get => _downloadRateCalc?.CurrentRate ?? _snapshotDownloadRate;

        set => _snapshotDownloadRate = value;

    }

    /// <summary>

    /// Current upload rate in bytes/second (total traffic).

    /// </summary>

    public double UploadRate

    {

        get => _uploadRateCalc?.CurrentRate ?? _snapshotUploadRate;

        set => _snapshotUploadRate = value;

    }

    // Payload only (actual file data)

    long IStatisticsTracker.PayloadDownloaded => Interlocked.Read(ref _payloadDownloaded);

    long IStatisticsTracker.PayloadUploaded => Interlocked.Read(ref _payloadUploaded);

    /// <summary>

    /// Current payload download rate in bytes/second.

    /// </summary>

    public double PayloadDownloadRate

    {

        get => _payloadDownloadRateCalc?.CurrentRate ?? _snapshotPayloadDownloadRate;

        set => _snapshotPayloadDownloadRate = value;

    }

    /// <summary>

    /// Current payload upload rate in bytes/second.

    /// </summary>

    public double PayloadUploadRate

    {

        get => _payloadUploadRateCalc?.CurrentRate ?? _snapshotPayloadUploadRate;

        set => _snapshotPayloadUploadRate = value;

    }

    /// <summary>

    /// Smoothed payload download rate for ETA calculations.

    /// Decays exponentially rather than dropping to 0 during network gaps.

    /// </summary>

    public double SmoothedPayloadDownloadRate

    {

        get => _payloadDownloadRateCalc?.SmoothedRate ?? _snapshotSmoothedPayloadDownloadRate;

        set => _snapshotSmoothedPayloadDownloadRate = value;

    }

    // Verified (hash-verified pieces written to disk)

    public long VerifiedDownloaded

    {

        get => Interlocked.Read(ref _verifiedDownloaded);

        set => Interlocked.Exchange(ref _verifiedDownloaded, value);

    }

    public double VerifiedDownloadRate

    {

        get => _verifiedDownloadRateCalc?.CurrentRate ?? _snapshotVerifiedDownloadRate;

        set => _snapshotVerifiedDownloadRate = value;

    }

    public int PiecesCompleted

    {

        get => Volatile.Read(ref _piecesCompleted);

        set => Volatile.Write(ref _piecesCompleted, value);

    }

    /// <summary>

    /// Piece verification progress (0.0 to 1.0). Only meaningful during Verifying state.

    /// </summary>

    public double VerificationProgress

    {

        get => Volatile.Read(ref _verificationProgress);

        set => Volatile.Write(ref _verificationProgress, value);

    }

    public int PiecesUploaded

    {

        get => Volatile.Read(ref _piecesUploaded);

        set => Volatile.Write(ref _piecesUploaded, value);

    }

    public long FailedBytes

    {

        get => Interlocked.Read(ref _failedBytes);

        set => Interlocked.Exchange(ref _failedBytes, value);

    }

    public long EndgameWastedBytes

    {

        get => Interlocked.Read(ref _endgameWastedBytes);

        set => Interlocked.Exchange(ref _endgameWastedBytes, value);

    }

    public int EndgameDuplicateBlocks

    {

        get => Volatile.Read(ref _endgameDuplicateBlocks);

        set => Volatile.Write(ref _endgameDuplicateBlocks, value);

    }

    public int TrackedPeerCount => _peerStats?.Count ?? 0;

    #endregion

    #region All-Time / Session Statistics (for persistence and UI)

    /// <summary>

    /// Total payload bytes ever downloaded for this torrent (all-time).

    /// Uses payload counter (actual file data) not total traffic counter

    /// which includes protocol overhead (handshakes, bitfield, PEX, keepalives, etc.).

    /// Mirrors AllTimeUploaded; libtorrent persists payload-only on both sides

    /// (m_total_downloaded in torrent.hpp).

    /// </summary>

    public long AllTimeDownloaded

    {

        get => Interlocked.Read(ref _allTimePayloadDownloaded);

        set => Interlocked.Exchange(ref _allTimePayloadDownloaded, value);

    }

    /// <summary>

    /// Total payload bytes ever uploaded for this torrent (all-time).

    /// Uses payload counter (actual file data) not total traffic counter

    /// which includes protocol overhead (keepalives, have, bitfield, etc.).

    /// </summary>

    public long AllTimeUploaded

    {

        get => Interlocked.Read(ref _allTimePayloadUploaded);

        set => Interlocked.Exchange(ref _allTimePayloadUploaded, value);

    }

    /// <summary>

    /// Payload bytes downloaded this session (actual file data).

    /// </summary>

    public long SessionPayloadDownloaded

    {

        get => Interlocked.Read(ref _payloadDownloaded);

        set => Interlocked.Exchange(ref _payloadDownloaded, value);

    }

    /// <summary>

    /// Payload bytes uploaded this session (actual file data).

    /// </summary>

    public long SessionPayloadUploaded

    {

        get => Interlocked.Read(ref _payloadUploaded);

        set => Interlocked.Exchange(ref _payloadUploaded, value);

    }

    /// <summary>

    /// Verified bytes downloaded this session (hash-verified pieces only).

    /// Alias for VerifiedDownloaded.

    /// </summary>

    public long SessionVerifiedDownloaded

    {

        get => VerifiedDownloaded;

        set => VerifiedDownloaded = value;

    }

    /// <summary>

    /// Total payload bytes ever downloaded (all-time).

    /// </summary>

    public long AllTimePayloadDownloaded

    {

        get => Interlocked.Read(ref _allTimePayloadDownloaded);

        set => Interlocked.Exchange(ref _allTimePayloadDownloaded, value);

    }

    /// <summary>

    /// Total payload bytes ever uploaded (all-time).

    /// </summary>

    public long AllTimePayloadUploaded

    {

        get => Interlocked.Read(ref _allTimePayloadUploaded);

        set => Interlocked.Exchange(ref _allTimePayloadUploaded, value);

    }

    /// <summary>

    /// Bytes re-downloaded due to piece rejection this session.

    /// </summary>

    public long RedundantBytes { get; set; }

    #endregion

    #region Progress Statistics

    /// <summary>

    /// Total bytes completed (verified pieces).

    /// </summary>

    public long TotalDone { get; set; }

    /// <summary>

    /// Total torrent size in bytes.

    /// </summary>

    public long TotalSize { get; set; }

    /// <summary>

    /// Bytes wanted (respects file priorities, excludes skipped files).

    /// </summary>

    public long TotalWanted { get; set; }

    /// <summary>

    /// Bytes wanted that are completed.

    /// </summary>

    public long TotalWantedDone { get; set; }

    /// <summary>

    /// Progress as fraction 0.0 to 1.0.

    /// </summary>

    public float Progress => TotalWanted > 0 ? Math.Clamp((float)TotalWantedDone / TotalWanted, 0f, 1f) : 0f;

    /// <summary>

    /// Progress in parts per million.

    /// </summary>

    public int ProgressPpm => TotalWanted > 0 ? Math.Clamp((int)((TotalWantedDone * 1_000_000L) / TotalWanted), 0, 1_000_000) : 0;

    /// <summary>

    /// Verified progress as fraction 0.0 to 1.0.

    /// </summary>

    public double VerifiedProgress => TotalPieces > 0 ? Math.Clamp((double)PiecesCompleted / TotalPieces, 0.0, 1.0) : 0.0;

    /// <summary>

    /// Number of pieces downloaded but not yet verified.

    /// </summary>

    public int PendingPieces { get; set; }

    /// <summary>

    /// Total number of pieces in torrent.

    /// </summary>

    public int TotalPieces { get; set; }

    #endregion

    #region Peer Statistics

    public int ConnectedPeers { get; set; }

    public int ConnectedSeeds { get; set; }

    public int TrackerSeeders { get; set; }

    public int TrackerLeechers { get; set; }

    public int KnownPeers { get; set; }

    public int ConnectCandidates { get; set; }

    public int UnchokedPeers { get; set; }

    public int InterestedPeers { get; set; }

    #endregion

    #region Time Statistics

    public DateTime AddedTime { get; set; }

    public DateTime? CompletedTime { get; set; }

    public DateTime? LastSeenComplete { get; set; }

    public DateTime? LastUpload { get; set; }

    public DateTime? LastDownload { get; set; }

    public TimeSpan ActiveDuration { get; set; }

    public TimeSpan FinishedDuration { get; set; }

    public TimeSpan SeedingDuration { get; set; }

    #endregion

    #region Tracker Statistics

    public TimeSpan? ReannounceIn { get; set; }

    public int AnnounceInterval { get; set; }

    public DateTime? LastAnnounce { get; set; }

    #endregion

    #region Distribution Statistics

    public float DistributedCopies { get; set; }

    public int DistributedFullCopies { get; set; }

    public int DistributedFraction { get; set; }

    public float Availability { get; set; }

    #endregion

    #region State Information

    public TransferPhase Phase { get; set; }

    public UserIntent Intent { get; set; }

    public TorrentError? Error { get; set; }

    public bool IsSeeding { get; set; }

    public bool IsFinished { get; set; }

    public bool HasMetadata { get; set; } = true;

    public bool NeedSaveResume { get; set; }

    public bool UserPaused { get; set; }

    #endregion

    #region Computed Properties

    public long BytesRemaining => TotalWanted - TotalWantedDone;

    /// <summary>

    /// Share ratio (payload uploaded / payload downloaded).

    /// </summary>

    public float Ratio => AllTimePayloadDownloaded > 0

        ? (float)AllTimePayloadUploaded / AllTimePayloadDownloaded

        : (AllTimePayloadUploaded > 0 ? float.MaxValue : 0f);

    public bool IsSeederSwarm { get; set; }

    public bool AutoSequentialActive { get; set; }

    public bool IsEndgame { get; set; }

    /// <summary>

    /// Average download rate over active duration (bytes/second).

    /// </summary>

    public double AverageDownloadRate => ActiveDuration.TotalSeconds > 0

        ? SessionPayloadDownloaded / ActiveDuration.TotalSeconds

        : 0;

    /// <summary>

    /// Average upload rate over active duration (bytes/second).

    /// </summary>

    public double AverageUploadRate => ActiveDuration.TotalSeconds > 0

        ? SessionPayloadUploaded / ActiveDuration.TotalSeconds

        : 0;

    /// <summary>

    /// Total wasted bytes (FailedBytes + EndgameWastedBytes + RedundantBytes).

    /// </summary>

    public long TotalWastedBytes => FailedBytes + EndgameWastedBytes + RedundantBytes;

    #endregion

    #region IStatisticsTracker - Recording Methods

    public void RegisterPeer(IPeerConnection peer)

    {

        if (peer == null || _peerStats == null) return;

        if (_peerStats.TryAdd(peer, new PeerTransferStats()))

        {

            _logger?.LogTrace("Registered peer {Peer} for statistics tracking", peer.PeerInfo.EndPoint);

        }

    }

    public void UnregisterPeer(IPeerConnection peer)

    {

        if (peer == null || _peerStats == null) return;

        if (_peerStats.TryRemove(peer, out var stats))

        {

            _logger?.LogTrace("Unregistered peer {Peer} - Downloaded: {Down}, Uploaded: {Up}",

                peer.PeerInfo.EndPoint, FormatBytes(stats.Downloaded), FormatBytes(stats.Uploaded));

        }

    }

    public void RecordDownload(IPeerConnection peer, int bytes)

    {

        if (bytes <= 0) return;

        Interlocked.Add(ref _totalDownloaded, bytes);

        Interlocked.Add(ref _sessionDownloaded, bytes);

        _downloadRateCalc?.AddSample(bytes);

        _accumulator?.AddDownload(bytes);

        if (peer != null && _peerStats != null && _peerStats.TryGetValue(peer, out var stats))

        {

            stats.AddDownload(bytes);

        }

    }

    public void RecordUpload(IPeerConnection peer, int bytes)

    {

        if (bytes <= 0) return;

        Interlocked.Add(ref _totalUploaded, bytes);

        Interlocked.Add(ref _sessionUploaded, bytes);

        _uploadRateCalc?.AddSample(bytes);

        _accumulator?.AddUpload(bytes);

        if (peer != null && _peerStats != null && _peerStats.TryGetValue(peer, out var stats))

        {

            stats.AddUpload(bytes);

        }

    }

    public void RecordPayloadDownload(IPeerConnection peer, int bytes)

    {

        if (bytes <= 0) return;

        Interlocked.Add(ref _payloadDownloaded, bytes);

        _payloadDownloadRateCalc?.AddSample(bytes);

        _accumulator?.AddPayloadDownload(bytes);

        if (peer != null && _peerStats != null && _peerStats.TryGetValue(peer, out var stats))

        {

            stats.AddPayloadDownload(bytes);

        }

    }

    public void RecordPayloadUpload(IPeerConnection peer, int bytes)

    {

        if (bytes <= 0) return;

        Interlocked.Add(ref _payloadUploaded, bytes);

        _payloadUploadRateCalc?.AddSample(bytes);

        _accumulator?.AddPayloadUpload(bytes);

        if (peer != null && _peerStats != null && _peerStats.TryGetValue(peer, out var stats))

        {

            stats.AddPayloadUpload(bytes);

        }

    }

    #region ITransferAccumulator

    void ITransferAccumulator.AddDownload(int bytes)

    {

        if (bytes <= 0) return;

        Interlocked.Add(ref _totalDownloaded, bytes);

    }

    void ITransferAccumulator.AddUpload(int bytes)

    {

        if (bytes <= 0) return;

        Interlocked.Add(ref _totalUploaded, bytes);

    }

    void ITransferAccumulator.AddPayloadDownload(int bytes)

    {

        if (bytes <= 0) return;

        Interlocked.Add(ref _allTimePayloadDownloaded, bytes);

    }

    void ITransferAccumulator.AddPayloadUpload(int bytes)

    {

        if (bytes <= 0) return;

        Interlocked.Add(ref _allTimePayloadUploaded, bytes);

    }

    long ITransferAccumulator.TotalPayloadDownloaded => AllTimePayloadDownloaded;

    long ITransferAccumulator.TotalPayloadUploaded => AllTimePayloadUploaded;

    #endregion

    public void RecordPieceCompleted()

    {

        Interlocked.Increment(ref _piecesCompleted);

    }

    public void RecordPieceUploaded()

    {

        Interlocked.Increment(ref _piecesUploaded);

    }

    public void RecordFailedBytes(long bytes)

    {

        if (bytes <= 0) return;

        Interlocked.Add(ref _failedBytes, bytes);

    }

    public void RecordVerifiedDownload(int bytes)

    {

        if (bytes <= 0) return;

        Interlocked.Add(ref _verifiedDownloaded, bytes);

        _verifiedDownloadRateCalc?.AddSample(bytes);

    }

    public void RecordEndgameWaste(int bytes)

    {

        if (bytes <= 0) return;

        Interlocked.Add(ref _endgameWastedBytes, bytes);

        Interlocked.Increment(ref _endgameDuplicateBlocks);

    }

    #endregion

    #region IStatisticsTracker - Per-Peer Queries

    public double GetPeerDownloadRate(IPeerConnection peer)

    {

        if (peer == null || _peerStats == null) return 0;

        return _peerStats.TryGetValue(peer, out var stats) ? stats.DownloadRate : 0;

    }

    public double GetPeerUploadRate(IPeerConnection peer)

    {

        if (peer == null || _peerStats == null) return 0;

        return _peerStats.TryGetValue(peer, out var stats) ? stats.UploadRate : 0;

    }

    public double GetPeerPayloadDownloadRate(IPeerConnection peer)

    {

        if (peer == null || _peerStats == null) return 0;

        return _peerStats.TryGetValue(peer, out var stats) ? stats.PayloadDownloadRate : 0;

    }

    public double GetPeerPayloadUploadRate(IPeerConnection peer)

    {

        if (peer == null || _peerStats == null) return 0;

        return _peerStats.TryGetValue(peer, out var stats) ? stats.PayloadUploadRate : 0;

    }

    public long GetPeerDownloaded(IPeerConnection peer)

    {

        if (peer == null || _peerStats == null) return 0;

        return _peerStats.TryGetValue(peer, out var stats) ? stats.Downloaded : 0;

    }

    public long GetPeerUploaded(IPeerConnection peer)

    {

        if (peer == null || _peerStats == null) return 0;

        return _peerStats.TryGetValue(peer, out var stats) ? stats.Uploaded : 0;

    }

    public long GetPeerPayloadDownloaded(IPeerConnection peer)

    {

        if (peer == null || _peerStats == null) return 0;

        return _peerStats.TryGetValue(peer, out var stats) ? stats.PayloadDownloaded : 0;

    }

    public long GetPeerPayloadUploaded(IPeerConnection peer)

    {

        if (peer == null || _peerStats == null) return 0;

        return _peerStats.TryGetValue(peer, out var stats) ? stats.PayloadUploaded : 0;

    }

    public IReadOnlyDictionary<IPeerConnection, PeerTransferStats> GetAllPeerStats()

    {

        return _peerStats ?? new ConcurrentDictionary<IPeerConnection, PeerTransferStats>();

    }

    #endregion

    #region IStatisticsTracker - Lifecycle

    public void InitializeFromExisting(long downloaded, long uploaded, int piecesCompleted)

    {

        Interlocked.Exchange(ref _totalDownloaded, downloaded);

        Interlocked.Exchange(ref _totalUploaded, uploaded);

        Interlocked.Exchange(ref _piecesCompleted, piecesCompleted);

        _logger?.LogInformation("Statistics initialized: Downloaded {Down}, Uploaded {Up}, Pieces {Pieces}",

            FormatBytes(downloaded), FormatBytes(uploaded), piecesCompleted);

    }

    public void ResetSession()

    {

        Interlocked.Exchange(ref _sessionDownloaded, 0);

        Interlocked.Exchange(ref _sessionUploaded, 0);

        Interlocked.Exchange(ref _payloadDownloaded, 0);

        Interlocked.Exchange(ref _payloadUploaded, 0);

        Interlocked.Exchange(ref _verifiedDownloaded, 0);

        Interlocked.Exchange(ref _failedBytes, 0);

        Interlocked.Exchange(ref _endgameWastedBytes, 0);

        Volatile.Write(ref _endgameDuplicateBlocks, 0);

        RedundantBytes = 0;

        IsEndgame = false;

        if (_peerStats != null)

        {

            foreach (var stats in _peerStats.Values)

            {

                stats.Reset();

            }

        }

        _logger?.LogDebug("Session statistics reset");

    }

    public void SetPaused(bool paused)

    {

        _downloadRateCalc?.SetPaused(paused);

        _uploadRateCalc?.SetPaused(paused);

        _payloadDownloadRateCalc?.SetPaused(paused);

        _payloadUploadRateCalc?.SetPaused(paused);

        _verifiedDownloadRateCalc?.SetPaused(paused);

        _logger?.LogDebug("Statistics pause state set to {Paused}", paused);

    }

    public void ResetRates()

    {

        _downloadRateCalc?.Reset();

        _uploadRateCalc?.Reset();

        _payloadDownloadRateCalc?.Reset();

        _payloadUploadRateCalc?.Reset();

        _verifiedDownloadRateCalc?.Reset();

        _logger?.LogDebug("All rate calculators reset");

    }

    #endregion

    #region Disk I/O Statistics (from former DiskStatisticsTracker)

    public long DiskBytesRead

    {

        get => Interlocked.Read(ref _diskBytesRead);

        set => Interlocked.Exchange(ref _diskBytesRead, value);

    }

    public long DiskReadOperations

    {

        get => Interlocked.Read(ref _diskReadOperations);

        set => Interlocked.Exchange(ref _diskReadOperations, value);

    }

    public long DiskBytesWritten

    {

        get => Interlocked.Read(ref _diskBytesWritten);

        set => Interlocked.Exchange(ref _diskBytesWritten, value);

    }

    public long DiskWriteOperations

    {

        get => Interlocked.Read(ref _diskWriteOperations);

        set => Interlocked.Exchange(ref _diskWriteOperations, value);

    }

    public int DiskPendingReads

    {

        get => Volatile.Read(ref _diskPendingReads);

        set => Volatile.Write(ref _diskPendingReads, value);

    }

    public int DiskPendingWrites

    {

        get => Volatile.Read(ref _diskPendingWrites);

        set => Volatile.Write(ref _diskPendingWrites, value);

    }

    public long DiskHashOperations

    {

        get => Interlocked.Read(ref _diskHashOperations);

        set => Interlocked.Exchange(ref _diskHashOperations, value);

    }

    public long DiskHashPassed

    {

        get => Interlocked.Read(ref _diskHashPassed);

        set => Interlocked.Exchange(ref _diskHashPassed, value);

    }

    public long DiskHashFailed

    {

        get => Interlocked.Read(ref _diskHashFailed);

        set => Interlocked.Exchange(ref _diskHashFailed, value);

    }

    public float DiskHashSuccessRate

    {

        get

        {

            var total = DiskHashOperations;

            return total > 0 ? (float)DiskHashPassed / total : 1.0f;

        }

    }

    public long DiskCacheHits

    {

        get => Interlocked.Read(ref _diskCacheHits);

        set => Interlocked.Exchange(ref _diskCacheHits, value);

    }

    public long DiskCacheMisses

    {

        get => Interlocked.Read(ref _diskCacheMisses);

        set => Interlocked.Exchange(ref _diskCacheMisses, value);

    }

    public float DiskCacheHitRatio

    {

        get

        {

            var total = DiskCacheHits + DiskCacheMisses;

            return total > 0 ? (float)DiskCacheHits / total : 0.0f;

        }

    }

    public void RecordDiskRead(long bytes)

    {

        Interlocked.Add(ref _diskBytesRead, bytes);

        Interlocked.Increment(ref _diskReadOperations);

    }

    public void RecordDiskWrite(long bytes)

    {

        Interlocked.Add(ref _diskBytesWritten, bytes);

        Interlocked.Increment(ref _diskWriteOperations);

    }

    public void RecordDiskHashVerification(bool passed)

    {

        Interlocked.Increment(ref _diskHashOperations);

        if (passed)

            Interlocked.Increment(ref _diskHashPassed);

        else

            Interlocked.Increment(ref _diskHashFailed);

    }

    public void IncrementPendingReads()

    {

        Interlocked.Increment(ref _diskPendingReads);

    }

    public void DecrementPendingReads()

    {

        Interlocked.Decrement(ref _diskPendingReads);

    }

    public void IncrementPendingWrites()

    {

        Interlocked.Increment(ref _diskPendingWrites);

    }

    public void DecrementPendingWrites()

    {

        Interlocked.Decrement(ref _diskPendingWrites);

    }

    public void RecordCacheHit()

    {

        Interlocked.Increment(ref _diskCacheHits);

    }

    public void RecordCacheMiss()

    {

        Interlocked.Increment(ref _diskCacheMisses);

    }

    #endregion

    #region Snapshot

    /// <summary>

    /// Create a snapshot of current statistics (for UI/persistence).

    /// Returns a lightweight copy without rate calculators or peer tracking.

    /// </summary>

    public TorrentStatistics CreateSnapshot()

    {

        return new TorrentStatistics

        {

            // Transfer stats (atomic reads)

            SessionDownloaded = this.SessionDownloaded,

            SessionUploaded = this.SessionUploaded,

            SessionPayloadDownloaded = this.SessionPayloadDownloaded,

            SessionPayloadUploaded = this.SessionPayloadUploaded,

            SessionVerifiedDownloaded = this.SessionVerifiedDownloaded,

            FailedBytes = this.FailedBytes,

            RedundantBytes = this.RedundantBytes,

            EndgameWastedBytes = this.EndgameWastedBytes,

            EndgameDuplicateBlocks = this.EndgameDuplicateBlocks,

            // All-time stats

            AllTimeDownloaded = this.AllTimeDownloaded,

            AllTimeUploaded = this.AllTimeUploaded,

            AllTimePayloadDownloaded = this.AllTimePayloadDownloaded,

            AllTimePayloadUploaded = this.AllTimePayloadUploaded,

            // Progress

            TotalDone = this.TotalDone,

            TotalSize = this.TotalSize,

            TotalWanted = this.TotalWanted,

            TotalWantedDone = this.TotalWantedDone,

            PendingPieces = this.PendingPieces,

            PiecesCompleted = this.PiecesCompleted,

            TotalPieces = this.TotalPieces,

            // Rates (snapshot from calculators)

            DownloadRate = this.DownloadRate,

            UploadRate = this.UploadRate,

            PayloadDownloadRate = this.PayloadDownloadRate,

            PayloadUploadRate = this.PayloadUploadRate,

            SmoothedPayloadDownloadRate = this.SmoothedPayloadDownloadRate,

            VerifiedDownloadRate = this.VerifiedDownloadRate,

            // Peers

            ConnectedPeers = this.ConnectedPeers,

            ConnectedSeeds = this.ConnectedSeeds,

            TrackerSeeders = this.TrackerSeeders,

            TrackerLeechers = this.TrackerLeechers,

            KnownPeers = this.KnownPeers,

            ConnectCandidates = this.ConnectCandidates,

            UnchokedPeers = this.UnchokedPeers,

            InterestedPeers = this.InterestedPeers,

            // Time

            AddedTime = this.AddedTime,

            CompletedTime = this.CompletedTime,

            LastSeenComplete = this.LastSeenComplete,

            LastUpload = this.LastUpload,

            LastDownload = this.LastDownload,

            ActiveDuration = this.ActiveDuration,

            FinishedDuration = this.FinishedDuration,

            SeedingDuration = this.SeedingDuration,

            // Tracker

            ReannounceIn = this.ReannounceIn,

            AnnounceInterval = this.AnnounceInterval,

            LastAnnounce = this.LastAnnounce,

            // Distribution

            DistributedCopies = this.DistributedCopies,

            DistributedFullCopies = this.DistributedFullCopies,

            DistributedFraction = this.DistributedFraction,

            Availability = this.Availability,

            // State dimensions

            Phase = this.Phase,

            Intent = this.Intent,

            IsSeeding = this.IsSeeding,

            IsFinished = this.IsFinished,

            HasMetadata = this.HasMetadata,

            NeedSaveResume = this.NeedSaveResume,

            UserPaused = this.UserPaused,

            IsSeederSwarm = this.IsSeederSwarm,

            AutoSequentialActive = this.AutoSequentialActive,

            IsEndgame = this.IsEndgame,

            // Disk I/O

            DiskBytesRead = this.DiskBytesRead,

            DiskReadOperations = this.DiskReadOperations,

            DiskBytesWritten = this.DiskBytesWritten,

            DiskWriteOperations = this.DiskWriteOperations,

            DiskPendingReads = this.DiskPendingReads,

            DiskPendingWrites = this.DiskPendingWrites,

            DiskHashOperations = this.DiskHashOperations,

            DiskHashPassed = this.DiskHashPassed,

            DiskHashFailed = this.DiskHashFailed,

            DiskCacheHits = this.DiskCacheHits,

            DiskCacheMisses = this.DiskCacheMisses

        };

    }

    #endregion

    #region Dispose

    public void Dispose()

    {

        _peerStats?.Clear();

        _logger?.LogDebug("TorrentStatistics disposed - Final: Down {Down}, Up {Up}",

            FormatBytes(AllTimeDownloaded), FormatBytes(AllTimeUploaded));

    }

    #endregion

    #region Helpers

    private static string FormatBytes(long bytes)

    {

        string[] sizes = { "B", "KB", "MB", "GB", "TB" };

        double len = bytes;

        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)

        {

            order++;

            len /= 1024;

        }

        return $"{len:0.##} {sizes[order]}";

    }

    #endregion

}
