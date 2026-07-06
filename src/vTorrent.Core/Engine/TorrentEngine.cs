using System;

using System.Collections;

using System.Collections.Concurrent;

using System.Collections.Generic;

using System.IO;

using System.Linq;

using System.Security.Cryptography;

using System.Text;

using System.Threading;

using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using vTorrent.Bencode.Parsers;

using vTorrent.Bencode.Torrents;

using vTorrent.Core.FileAllocator;

using vTorrent.Core.PeerCommunication.Models;

using vTorrent.Core.PeerCommunication.Utilities;

using vTorrent.Core.PieceIO;
using vTorrent.Core.PieceIO.Backends;

using vTorrent.Core.TrackerCommunication;

using vTorrent.Core.Download;
using vTorrent.Core.Upload;
using vTorrent.Core.PeerCommunication;
using vTorrent.Core.PeerCommunication.Extensions;

namespace vTorrent.Core.Engine;

using PeerSettings = vTorrent.Abstractions.Settings.PeerSettings;
using TrackerSettings = vTorrent.Abstractions.Settings.TrackerSettings;

using vTorrent.Core.Session;

using vTorrent.Core.ResumeData;

using vTorrent.Core.Interfaces;

using vTorrent.Storage;

using vTorrent.Core.PeerCommunication.Bandwidth;

using vTorrent.Core.PeerCommunication.Transport;

using vTorrent.Core.Merkle;

using vTorrent.Abstractions.Enums;

using vTorrent.Core.State;

using vTorrent.Abstractions.Events;

using vTorrent.Abstractions.Interfaces;

using vTorrent.Abstractions.Interfaces.Engine;

using vTorrent.Abstractions.Interfaces.Transport;

using vTorrent.Abstractions.Models;

using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Storage;

public class TorrentEngine : IDisposable

{

    private readonly ILogger<TorrentEngine> _logger;

    private readonly ILoggerFactory _loggerFactory;

    private readonly Torrent _torrent;

    private string _downloadPath;  // Mutable for move_storage support

    // Settings

    private readonly PeerSettings _peerSettings;

    private readonly IOptionsMonitor<TrackerSettings> _trackerMonitor;

    // Bandwidth limiting

    private readonly IPeerBandwidthLimiter _bandwidthLimiter;

    // Database for peer cache persistence

    private readonly TorrentDatabase _database;

    // Transfer accumulator for all-time counters (owned by ManagedTorrent.Statistics)

    private readonly ITransferAccumulator _transferAccumulator;

    // Transport connector (null = use TcpTransportConnector; set = use uTP-first connector)

    private readonly ITransportConnector? _transportConnector;

    // BEP 24: External IP voter for multi-source consensus

    private readonly IExternalIpVoter? _externalIpVoter;

    // Resume data provider (optional, for fast resume)

    private IResumeDataProvider? _resumeDataProvider;

    // Phase 1

    private IFileAllocator _fileAllocator;

    // Phase 2

    private IDiskBackend? _diskBackend;

    private IPieceManager _pieceManager;

    private PeerRegistry _peerRegistry;

    private IPeerManager _peerManager;

    // _torrentStatistics serves as both the IStatisticsTracker and the snapshot source

    private PeerCache _peerCache;

    // Phase 3

    private ITrackerManager _trackerManager;

    // Phase 4

    private PeerMessageRouter _messageRouter;

    // Phase 5

    private ChokingManager _chokingManager;

    private DownloadCoordinator _downloadCoordinator;

    private UploadCoordinator _uploadCoordinator;

    private PeerSendBufferManager? _sendBufferManager;

    private PeerReplacer _peerProber;

    private SuperSeedManager? _superSeedManager;

    private SeederSwarmDetector _seederSwarmDetector;

    private TorrentStatistics _torrentStatistics;

    private FileProgressTracker _fileProgressTracker;

    private WebSeedManager? _webSeedManager;

    private readonly Network.UdpSocketManager? _udpSocketManager;
    private readonly TrackerCommunication.Udp.UdpTrackerPacketHandler? _trackerPacketHandler;

    private Bitfield _localBitfield;

    private readonly CancellationTokenSource _stopCts = new();

    private Task _mainTask;

    private bool _disposed;

    // Engine-owned pause state. Intent (UserIntent.Paused) is NOT a reliable proxy:
    // queue transitions flip intent Paused→Queued while the engine's transfer loops
    // remain stopped, which previously made IsPaused report false and left the
    // torrent permanently Queued (StartTorrentInternal refused to start, ResumeAsync
    // refused to resume).
    private volatile bool _transfersPaused;

    // Re-entrancy gate for ResumeAsync: user-initiated resume and AutoManager
    // slot grants can race; the loser must no-op (PeerManager.StartAsync throws
    // if already running, which would post a false engine error).
    private int _resumeInProgress;

    // Stats

    private DateTime _startTime;

    private readonly EngineStatistics _stats;

    private readonly EnginePhaseInitializer _phaseInitializer;

    private EngineFileManager _fileManager;

    private EngineSettingsApplier _settingsApplier;

    private IOptionsMonitor<BehaviorSettings> _behaviorMonitor;

    private IOptionsMonitor<PeerSettings> _peerSettingsMonitor;

    private readonly IOptionsMonitor<PrivacySettings> _privacyMonitor;

    // BEP 54: tracks DontHaveExtension per peer for broadcasting

    private readonly ConcurrentDictionary<IPeerConnection, DontHaveExtension> _dontHaveExtensions = new();

    // BEP 55: holepunch NAT traversal manager

    private HolepunchManager? _holepunchManager;

    // Background verification state
    private TaskCompletionSource _verificationDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _verifiedPieceCount;

    #region Internal Accessors (for EngineStatistics)

    internal DownloadCoordinator DownloadCoordinatorInternal => _downloadCoordinator;
    internal void SetDownloadCoordinator(DownloadCoordinator value) => _downloadCoordinator = value;

    internal ChokingManager ChokingManagerInternal => _chokingManager;
    internal void SetChokingManager(ChokingManager value) => _chokingManager = value;

    internal IPeerManager PeerManagerInternal => _peerManager;
    internal void SetPeerManager(IPeerManager value) => _peerManager = value;

    internal ITrackerManager TrackerManagerInternal => _trackerManager;
    internal void SetTrackerManager(ITrackerManager value) => _trackerManager = value;

    internal SeederSwarmDetector SeederSwarmDetectorInternal => _seederSwarmDetector;
    internal void SetSeederSwarmDetector(SeederSwarmDetector value) => _seederSwarmDetector = value;

    internal FileProgressTracker FileProgressTrackerInternal => _fileProgressTracker;
    internal void SetFileProgressTracker(FileProgressTracker value) => _fileProgressTracker = value;

    internal TorrentStatistics TorrentStatisticsInternal => _torrentStatistics;
    internal void SetTorrentStatistics(TorrentStatistics value) => _torrentStatistics = value;

    internal Bitfield LocalBitfieldInternal => _localBitfield;
    internal void SetLocalBitfield(Bitfield value) => _localBitfield = value;

    internal IFileAllocator FileAllocatorInternal => _fileAllocator;
    internal void SetFileAllocator(IFileAllocator value) => _fileAllocator = value;

    internal IDiskBackend? DiskBackendInternal => _diskBackend;
    internal void SetDiskBackend(IDiskBackend value) => _diskBackend = value;

    internal IPieceManager PieceManagerInternal => _pieceManager;
    internal void SetPieceManager(IPieceManager value) => _pieceManager = value;

    internal PeerRegistry PeerRegistryInternal => _peerRegistry;
    internal void SetPeerRegistry(PeerRegistry value) => _peerRegistry = value;

    internal PeerCache PeerCacheInternal => _peerCache;
    internal void SetPeerCache(PeerCache value) => _peerCache = value;

    internal PeerMessageRouter MessageRouterInternal => _messageRouter;
    internal void SetMessageRouter(PeerMessageRouter value) => _messageRouter = value;

    internal UploadCoordinator UploadCoordinatorInternal => _uploadCoordinator;
    internal void SetUploadCoordinator(UploadCoordinator value) => _uploadCoordinator = value;

    internal PeerSendBufferManager? SendBufferManagerInternal => _sendBufferManager;
    internal void SetSendBufferManager(PeerSendBufferManager? value) => _sendBufferManager = value;

    internal CancellationToken StopToken => _stopCts.Token;

    internal PeerReplacer PeerProberInternal => _peerProber;
    internal void SetPeerProber(PeerReplacer value) => _peerProber = value;

    internal SuperSeedManager? SuperSeedManagerInternal => _superSeedManager;
    internal void SetSuperSeedManager(SuperSeedManager? value) => _superSeedManager = value;

    /// <summary>Whether this torrent is in seed mode (lazy verification on upload).</summary>
    internal bool IsSeedMode { get; set; }

    /// <summary>Tracks which pieces have been hash-verified during seed mode upload.</summary>
    internal Bitfield? SeedModeVerifiedPieces { get; set; }

    private SeedModeVerifier? _seedModeVerifier;
    internal SeedModeVerifier? SeedModeVerifierInternal => _seedModeVerifier;
    internal void SetSeedModeVerifier(SeedModeVerifier? value) => _seedModeVerifier = value;

    internal ILoggerFactory LoggerFactoryInternal => _loggerFactory;

    internal FilePriority[]? PendingFilePriorities { get; private set; }
    internal void SetPendingFilePriorities(FilePriority[]? value) => PendingFilePriorities = value;

    internal string DownloadPathInternal => _downloadPath;

    internal PeerSettings PeerSettingsInternal => _peerSettings;

    internal IOptionsMonitor<TrackerSettings> TrackerMonitorInternal => _trackerMonitor;

    internal IPeerBandwidthLimiter BandwidthLimiterInternal => _bandwidthLimiter;

    internal TorrentDatabase DatabaseInternal => _database;

    internal ITransferAccumulator TransferAccumulatorInternal => _transferAccumulator;

    internal ITransportConnector? TransportConnectorInternal => _transportConnector;

    internal IExternalIpVoter? ExternalIpVoterInternal => _externalIpVoter;

    internal IResumeDataProvider? ResumeDataProviderInternal => _resumeDataProvider;

    internal bool SequentialDownloadSettingInternal => _sequentialDownloadSetting;

    internal WebSeedManager? WebSeedManagerInternal => _webSeedManager;
    internal void SetWebSeedManager(WebSeedManager? value) => _webSeedManager = value;

    internal IOptionsMonitor<BehaviorSettings>? BehaviorMonitorInternal => _behaviorMonitor;
    internal IOptionsMonitor<PeerSettings>? PeerSettingsMonitorInternal => _peerSettingsMonitor;

    internal HashPicker? HashPickerInternal { get; private set; }
    internal void SetHashPicker(HashPicker? value) => HashPickerInternal = value;

    internal IHashExchangeHandler? HashExchangeHandlerInternal { get; private set; }
    internal void SetHashExchangeHandler(IHashExchangeHandler? value) => HashExchangeHandlerInternal = value;

    internal Dictionary<SHA256Hash, MerkleTree>? MerkleTreesInternal { get; private set; }
    internal void SetMerkleTrees(Dictionary<SHA256Hash, MerkleTree>? value) => MerkleTreesInternal = value;

    internal IOptionsMonitor<WebSeedSettings> WebSeedMonitorInternal { get; private set; }
    // Convenience accessor for snapshot reads (used by EnginePhaseInitializer)
    internal WebSeedSettings WebSeedSettingsInternal => WebSeedMonitorInternal.CurrentValue;

    internal DiskSettings DiskSettingsInternal { get; private set; } = new();
    internal void SetDiskSettings(DiskSettings value) => DiskSettingsInternal = value;

    internal IOptionsMonitor<DiskSettings>? DiskMonitorInternal { get; private set; }
    internal void SetDiskMonitor(IOptionsMonitor<DiskSettings>? value) => DiskMonitorInternal = value;

    internal IOptionsMonitor<EncryptionSettings> EncryptionMonitorInternal { get; private set; }

    internal IOptionsMonitor<ConnectionSettings> ConnectionMonitorInternal { get; private set; }

    internal IOptionsMonitor<PrivacySettings>? PrivacyMonitorInternal => _privacyMonitor;

    // Proxy settings (threaded from the orchestrator's proxy monitor) so engine-level HTTP
    // clients — notably web seeds / HTTP seeds — can route through the configured proxy instead
    // of leaking out over the real connection. Null when no proxy monitor was supplied.
    internal IOptionsMonitor<ProxySettings>? ProxyMonitorInternal { get; private set; }

    internal vTorrent.Core.Network.PeerClass.PeerClassManager? PeerClassManagerInternal { get; private set; }

    // Global unchoke slot allocator (threaded from ResourceAllocator via EngineFactory)
    internal vTorrent.Core.Orchestration.UnchokeAllocator? UnchokeAllocatorInternal { get; private set; }

    internal Network.UdpSocketManager? UdpSocketManagerInternal => _udpSocketManager;
    internal TrackerCommunication.Udp.UdpTrackerPacketHandler? TrackerPacketHandlerInternal => _trackerPacketHandler;

    internal EngineFileManager FileManagerInternal => _fileManager;

    internal ConcurrentDictionary<IPeerConnection, DontHaveExtension> DontHaveExtensions => _dontHaveExtensions;

    // Disk write throttler for backpressure (per-torrent, wired in Phase 2)
    internal DiskWriteThrottler? DiskWriteThrottlerInternal { get; private set; }
    internal void SetDiskWriteThrottler(DiskWriteThrottler? value) => DiskWriteThrottlerInternal = value;

    // BEP 55: holepunch manager accessor for EnginePhaseInitializer
    internal HolepunchManager? HolepunchManagerInternal => _holepunchManager;
    internal void SetHolepunchManager(HolepunchManager? value) => _holepunchManager = value;

    // I2P service and settings (threaded from EngineFactory for TrackerClientFactory wiring)
    internal vTorrent.Core.Network.I2P.I2pService? I2pServiceInternal { get; set; }
    internal IOptionsMonitor<vTorrent.Abstractions.Settings.I2pSettings>? I2pSettingsMonitorInternal { get; private set; }

    // ManagedTorrent back-reference (set by EngineFactory after construction)
    internal vTorrent.Core.Orchestration.ManagedTorrent? ManagedTorrentInternal { get; set; }

    // Controller shortcut — null until ManagedTorrentInternal is set
    private TorrentStateController? StateController => ManagedTorrentInternal?.StateController;

    // Convenience read-through properties for guards / hot-path checks
    private TransferPhase CurrentPhase => StateController?.GetStatus().Phase ?? TransferPhase.Idle;
    private bool IsPausedState => StateController?.GetStatus().Intent == UserIntent.Paused;

    #endregion

    /// <summary>
    /// Called by EnginePhaseInitializer for each piece that passes hash verification during
    /// background verification. Atomically increments the verified piece counter and fires
    /// the PieceVerified event so subscribers (e.g. HAVE broadcast wiring) can react.
    /// </summary>
    internal void OnPieceVerified(int pieceIndex)
    {
        Interlocked.Increment(ref _verifiedPieceCount);
        PieceVerified?.Invoke(pieceIndex);
    }

    /// <summary>
    /// Broadcasts DONTHAVE for a piece to all connected peers that support lt_donthave.
    /// </summary>
    internal void BroadcastDontHave(int pieceIndex)
    {
        foreach (var (peer, ext) in _dontHaveExtensions)
        {
            if (!peer.IsConnected)
                continue;

            _ = ext.SendDontHaveAsync(pieceIndex);
        }
    }

    // Events

    public event EventHandler<TorrentProgressEventArgs> ProgressChanged;

    public event EventHandler<PeersDiscoveredEventArgs> PeersDiscovered;

    public event EventHandler DownloadCompleted;

    /// <summary>
    /// Raised when file validation detects missing or undersized files at the save path.
    /// The orchestrator subscribes to set TorrentStatus.MissingFiles = true.
    /// </summary>
    public event EventHandler<MissingFilesEventArgs>? MissingFilesDetected;

    internal void RaiseMissingFilesDetected(string message, List<(string path, long expectedSize, long actualSize)> files)
    {
        MissingFilesDetected?.Invoke(this, new MissingFilesEventArgs(message, files));
    }

    public event EventHandler<IntegrityVerificationEventArgs> IntegrityVerificationCompleted;

    /// <summary>
    /// Fired for each piece verified during background verification.
    /// Subscribers can use this to broadcast HAVE messages to connected peers.
    /// </summary>
    public event Action<int>? PieceVerified;

    /// <summary>
    /// Whether background piece verification has completed (or was skipped via fast resume).
    /// </summary>
    public bool IsVerificationComplete => _verificationDone.Task.IsCompleted;

    /// <summary>
    /// Number of pieces verified so far during background verification.
    /// Incremented atomically as each piece passes hash check.
    /// </summary>
    public int VerifiedPieceCount => _verifiedPieceCount;

    /// <summary>
    /// Awaitable task that completes when background verification finishes.
    /// Components that truly need to wait for full verification can await this.
    /// </summary>
    internal TaskCompletionSource VerificationDone => _verificationDone;

    // Core Properties

    public Torrent Torrent => _torrent;

    /// <summary>Orthogonal transfer phase dimension.</summary>
    public TransferPhase Phase => CurrentPhase;

    /// <summary>Current engine error, if any.</summary>
    public TorrentError? EngineError => StateController?.GetStatus().Error;

    /// <summary>Whether the engine is paused (orthogonal to phase).</summary>
    // Flag-first with intent fallback: strictly more permissive than the old
    // intent-only check, so no legacy path regresses while queued-paused engines
    // are now correctly reported as paused.
    public bool IsPaused => _transfersPaused || IsPausedState;

    public string InfoHashHex => _torrent.GetInfoHashHex();

    public string Name => _torrent.DisplayName;

    public long TotalSize => _torrent.TotalSize;

    public int PieceCount => _torrent.PieceCount;

    /// <summary>

    /// Grouped statistics view (Phase 5 extraction).

    /// All stat properties are also accessible directly on TorrentEngine for backward compatibility.

    /// </summary>

    public EngineStatistics Stats => _stats;

    #region Statistics (delegated to EngineStatistics)

    public double Progress => _stats.Progress;

    public int PiecesCompleted => _stats.PiecesCompleted;

    public long BytesInProgress => _stats.BytesInProgress;

    public long BytesEffective => _stats.BytesEffective;

    public long BytesRemaining => _stats.BytesRemaining;

    public long TotalUploaded => _stats.TotalUploaded;

    public long TotalDownloaded => _stats.TotalDownloaded;

    public int UnchokedPeers => _stats.UnchokedPeers;

    public int ConnectedPeers => _stats.ConnectedPeers;

    public int ConnectedSeeds => _stats.ConnectedSeeds;

    public int TotalSeeders => _stats.TotalSeeders;

    public int TotalLeechers => _stats.TotalLeechers;

    public DateTime? LastAnnounce => _stats.LastAnnounce;

    public int AnnounceInterval => _stats.AnnounceInterval;

    public TimeSpan? TimeToNextAnnounce => _stats.TimeToNextAnnounce;

    public bool IsSeederSwarm => _stats.IsSeederSwarm;

    public FileProgressTracker FileProgress => _stats.FileProgress;

    public float Availability => _stats.Availability;

    public long BytesDownloaded => _stats.BytesDownloaded;

    public long BytesUploaded => _stats.BytesUploaded;

    public double DownloadRate => _stats.DownloadRate;

    public double UploadRate => _stats.UploadRate;

    public long PayloadDownloaded => _stats.PayloadDownloaded;

    public long PayloadUploaded => _stats.PayloadUploaded;

    /// <summary>

    /// All-time payload bytes for tracker announces (BEP 3).

    /// Reads from ITransferAccumulator (ManagedTorrent.Statistics) when available,

    /// falls back to session payload counters.

    /// </summary>

    public long TrackerPayloadUploaded => _transferAccumulator?.TotalPayloadUploaded ?? PayloadUploaded;

    public long TrackerPayloadDownloaded => _transferAccumulator?.TotalPayloadDownloaded ?? PayloadDownloaded;

    public double PayloadDownloadRate => _stats.PayloadDownloadRate;

    public double PayloadUploadRate => _stats.PayloadUploadRate;

    public double SmoothedPayloadDownloadRate => _stats.SmoothedPayloadDownloadRate;

    public long VerifiedDownloaded => _stats.VerifiedDownloaded;

    public double VerifiedDownloadRate => _stats.VerifiedDownloadRate;

    public bool IsEndgameMode => _stats.IsEndgameMode;

    public long EndgameWastedBytes => _stats.EndgameWastedBytes;

    public int EndgameDuplicateBlocks => _stats.EndgameDuplicateBlocks;

    public long FailedBytes => _stats.FailedBytes;

    public TorrentStatusSnapshot GetStatus() => _stats.GetStatus();

    public BitArray? GetPieceBitfield() => _stats.GetPieceBitfield();

    #endregion

    private readonly int _peerKey;

    // Sequential download setting (applied when DownloadCoordinator is created)

    private readonly bool _sequentialDownloadSetting;

    public TorrentEngine(

        Torrent torrent,

        string downloadPath,

        PeerSettings peerSettings,

        IOptionsMonitor<TrackerSettings> trackerMonitor,

        ILoggerFactory loggerFactory,

        ITorrentDialog torrentDialog,

        bool sequentialDownload = false,

        IPeerBandwidthLimiter bandwidthLimiter = null,

        TorrentDatabase database = null,

        ITransferAccumulator transferAccumulator = null,

        ITransportConnector transportConnector = null,

        IExternalIpVoter? externalIpVoter = null,

        IOptionsMonitor<BehaviorSettings> behaviorMonitor = null,

        IOptionsMonitor<PeerSettings> peerSettingsMonitor = null,

        IOptionsMonitor<EncryptionSettings> encryptionMonitor = null,

        IOptionsMonitor<ConnectionSettings> connectionMonitor = null,

        IOptionsMonitor<WebSeedSettings>? webSeedMonitor = null,

        IOptionsMonitor<PrivacySettings>? privacyMonitor = null,

        IOptionsMonitor<DiskSettings>? diskMonitor = null,

        vTorrent.Core.Network.PeerClass.PeerClassManager? peerClassManager = null,

        vTorrent.Core.Orchestration.UnchokeAllocator? unchokeAllocator = null,

        vTorrent.Core.Network.UdpSocketManager? udpSocketManager = null,

        vTorrent.Core.TrackerCommunication.Udp.UdpTrackerPacketHandler? trackerPacketHandler = null,

        IOptionsMonitor<vTorrent.Abstractions.Settings.I2pSettings>? i2pSettingsMonitor = null,

        IOptionsMonitor<ProxySettings>? proxyMonitor = null)

    {

        _torrent = torrent ?? throw new ArgumentNullException(nameof(torrent));

        _downloadPath = downloadPath ?? throw new ArgumentNullException(nameof(downloadPath));

        _peerSettings = peerSettings ?? throw new ArgumentNullException(nameof(peerSettings));

        _trackerMonitor = trackerMonitor ?? throw new ArgumentNullException(nameof(trackerMonitor));

        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

        _logger = loggerFactory.CreateLogger<TorrentEngine>();

        _sequentialDownloadSetting = sequentialDownload;

        _bandwidthLimiter = bandwidthLimiter;

        _behaviorMonitor = behaviorMonitor;

        _peerSettingsMonitor = peerSettingsMonitor;

        EncryptionMonitorInternal = encryptionMonitor ?? new OptionsMonitorShim<EncryptionSettings>(new EncryptionSettings());

        ConnectionMonitorInternal = connectionMonitor ?? new OptionsMonitorShim<ConnectionSettings>(new ConnectionSettings());

        WebSeedMonitorInternal = webSeedMonitor ?? new OptionsMonitorShim<WebSeedSettings>(new WebSeedSettings());

        _privacyMonitor = privacyMonitor;

        ProxyMonitorInternal = proxyMonitor;

        DiskMonitorInternal = diskMonitor;

        _database = database;

        _transferAccumulator = transferAccumulator;

        _transportConnector = transportConnector;

        _externalIpVoter = externalIpVoter;

        PeerClassManagerInternal = peerClassManager;

        UnchokeAllocatorInternal = unchokeAllocator;

        _udpSocketManager = udpSocketManager;
        _trackerPacketHandler = trackerPacketHandler;

        I2pSettingsMonitorInternal = i2pSettingsMonitor;

        _peerKey = RandomNumberGenerator.GetInt32(int.MaxValue);

        _stats = new EngineStatistics(this);

        _phaseInitializer = new EnginePhaseInitializer(this, _logger);

        _settingsApplier = new EngineSettingsApplier(
            () => _downloadCoordinator,
            () => _chokingManager,
            () => _peerManager,
            () => _pieceManager,
            () => _fileProgressTracker,
            () => _torrentStatistics,
            () => _diskBackend,       // NEW
            _peerSettings,
            _logger);

        _logger.LogInformation("TorrentEngine created for {Name} [{InfoHash}], sequential={Sequential}",

            torrent.DisplayName, torrent.GetInfoHashHex(), sequentialDownload);

    }

    /// <summary>

    /// Enable or disable sequential download mode.

    /// When enabled, pieces are downloaded in order (0→N) instead of rarest-first.

    /// </summary>

    public void SetSequentialDownload(bool enabled) => _settingsApplier.SetSequentialDownload(enabled);

    /// <summary>

    /// Whether sequential download mode is enabled.

    /// </summary>

    public bool IsSequentialDownload => _downloadCoordinator?.IsSequentialMode ?? false;

    /// <summary>

    /// Enable or disable first/last piece priority for each file.

    /// When enabled, the first and last pieces of every file are downloaded first,

    /// which is useful for media preview / streaming.

    /// </summary>

    public void SetFirstLastPiecePriority(bool enabled) => _settingsApplier.SetFirstLastPiecePriority(enabled);

    // ── Streaming API (libtorrent-style piece deadlines) ──────────────

    /// <summary>

    /// Set a deadline for a specific piece (streaming API).

    /// The piece will be prioritized over non-critical pieces. When this is the first

    /// deadline, non-critical outstanding requests are cancelled to free bandwidth.

    /// libtorrent equivalent: torrent_handle::set_piece_deadline().

    /// </summary>

    public void SetPieceDeadline(int pieceIndex, int deadlineMs, bool alertWhenAvailable = false)
        => _settingsApplier.SetPieceDeadline(pieceIndex, deadlineMs, alertWhenAvailable);

    /// <summary>Remove deadline from a specific piece.</summary>
    public void ResetPieceDeadline(int pieceIndex) => _settingsApplier.ResetPieceDeadline(pieceIndex);

    /// <summary>Remove all piece deadlines.</summary>
    public void ClearPieceDeadlines() => _settingsApplier.ClearPieceDeadlines();

    /// <summary>Whether streaming mode is active (any deadlines set).</summary>

    public bool IsStreaming => _downloadCoordinator?.IsStreaming ?? false;

    /// <summary>

    /// Apply updated settings to the running torrent engine.

    /// This allows settings changes to take effect immediately without restart.

    /// </summary>

    /// <param name="maxUploadsPerTorrent">Maximum upload slots (unchoked peers)</param>

    /// <param name="enablePex">Enable Peer Exchange protocol</param>

    /// <param name="unchokeIntervalSeconds">Rechoking interval in seconds</param>

    /// <param name="optimisticUnchokeIntervalSeconds">Optimistic unchoke rotation interval in seconds</param>

    /// <param name="closeRedundantConnections">Whether to close redundant seed-to-seed connections</param>

    /// <param name="autoSequentialInSeederSwarm">Whether to auto-enable sequential in seeder swarm</param>

    /// <param name="prioritizePartialPieces">Whether to prioritize partial pieces over rare pieces</param>

    /// <param name="strictEndgameMode">Whether to limit endgame to 1 duplicate request per peer</param>

    /// <param name="seedingOutgoingConnections">Whether to make outgoing connections while seeding</param>

    public void ApplySettings(
        int? maxUploadsPerTorrent = null,
        bool? enablePex = null,
        int? unchokeIntervalSeconds = null,
        int? optimisticUnchokeIntervalSeconds = null,
        bool? closeRedundantConnections = null,
        bool? autoSequentialInSeederSwarm = null,
        bool? prioritizePartialPieces = null,
        bool? strictEndgameMode = null,
        bool? seedingOutgoingConnections = null)
        => _settingsApplier.ApplySettings(
            maxUploadsPerTorrent, enablePex, unchokeIntervalSeconds,
            optimisticUnchokeIntervalSeconds, closeRedundantConnections,
            autoSequentialInSeederSwarm, prioritizePartialPieces, strictEndgameMode,
            seedingOutgoingConnections);

    /// <summary>

    /// Starts the torrent download/seed process.

    /// </summary>

    public async Task StartAsync(CancellationToken cancellationToken = default)

    {

        if (CurrentPhase != TransferPhase.Idle)

            throw new InvalidOperationException($"Cannot start torrent in phase {CurrentPhase} (paused={IsPausedState})");

        _transfersPaused = false;

        _logger.LogInformation("Starting torrent: {Name}", _torrent.DisplayName);

        // Linked CTS so we can cancel background verification if a later phase fails
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try

        {

            // === PHASE 1: File Allocation ===
            // libtorrent parity: skip the visible Allocating phase when resume data exists.
            // Files are already on disk — showing "Allocating" is misleading for seeding/resumed torrents.
            // libtorrent goes directly to checking_resume_data → checking_files → seeding.

            if (_resumeDataProvider == null)
                StateController?.PostPhase(PhaseTrigger.Allocate);
            else
                StateController?.PostPhase(PhaseTrigger.CheckResume);

            await _phaseInitializer.InitializePhase1_FileAllocationAsync(startupCts.Token).ConfigureAwait(false);

            // === PHASE 2: Core Components (parallelizable) ===

            await _phaseInitializer.InitializePhase2_CoreComponentsAsync(startupCts.Token).ConfigureAwait(false);

            // Create EngineFileManager now that _pieceManager is available
            _fileManager = new EngineFileManager(
                _torrent, _pieceManager, _logger,
                () => _downloadPath,
                () => _localBitfield,
                () => _resumeDataProvider);

            // === PHASE 3: Tracker + Verification (parallel) ===

            StateController?.PostPhase(PhaseTrigger.CheckFiles);

            // Start tracker announces immediately — peers accumulate while verification runs
            if (_trackerManager != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _trackerManager.StartAsync(startupCts.Token).ConfigureAwait(false);
                        await _trackerManager.AnnounceStartedAsync(BytesRemaining, startupCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Initial tracker announce failed — will retry on schedule");
                    }
                }, startupCts.Token);
            }

            var fastResumeSucceeded = await _phaseInitializer.TryFastResumePhase3Async(startupCts.Token).ConfigureAwait(false);

            _logger.LogWarning("[DIAG] Phase 3 result: fastResumeSucceeded={Success}, bitfield ref={Ref}, complete={Complete}/{Total}, IsComplete={IsComplete}",
                fastResumeSucceeded,
                _localBitfield?.GetHashCode() ?? 0,
                _localBitfield?.CompletePieces ?? -1,
                _localBitfield?.PieceCount ?? -1,
                _localBitfield?.IsComplete ?? false);

            if (fastResumeSucceeded)

            {

                _phaseInitializer.SyncPieceManagerBitfield();

                _verificationDone.TrySetResult();

                _verifiedPieceCount = _localBitfield.CompletePieces;

                _logger.LogDebug("Phase 3 complete (FAST RESUME): {Verified}/{Total} pieces",

                    _localBitfield.CompletePieces, _torrent.PieceCount);

            }

            else

            {

                _logger.LogDebug("Phase 3: Starting piece verification of {Count} pieces", _torrent.PieceCount);

                _phaseInitializer.StartBackgroundVerification(startupCts.Token);

                // libtorrent parity: wait for verification to complete before proceeding.
                // Without this, the engine enters Downloading with an empty bitfield and
                // re-downloads pieces that already exist on disk (wastes bandwidth).
                // libtorrent's files_checked() is only called AFTER all pieces are hashed.
                await _verificationDone.Task.ConfigureAwait(false);

                _logger.LogDebug("Phase 3 complete (VERIFICATION): {Verified}/{Total} pieces",
                    _localBitfield.CompletePieces, _torrent.PieceCount);

            }

            // === PHASE 4: Network Components (tracker already running) ===

            StateController?.PostPhase(PhaseTrigger.Connect);

            // Clear MissingFiles: files were validated during Phase 3 verification.
            // The flag may have been set transiently during TryFastResumePhase3 before
            // files were allocated. Now that we're past verification, clear it.
            StateController?.PostMissingFiles(false);

            await _phaseInitializer.InitializePhase4_NetworkAsync(startupCts.Token).ConfigureAwait(false);

            // === PHASE 5: Coordinators ===

            _phaseInitializer.InitializePhase5_Coordinators();

            // === PHASE 6: Wire Everything ===

            _phaseInitializer.InitializePhase6_WireMessageHandlers();

            // === PHASE 7: Start Operations ===

            await StartOperationsAsync(startupCts.Token).ConfigureAwait(false);

            _logger.LogInformation("Torrent started successfully");

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Failed to start torrent");

            // Cancel background verification if it's still running
            startupCts.Cancel();

            // Resolve the TCS so nothing hangs waiting for verification
            _verificationDone.TrySetException(ex);

            StateController?.PostError(new TorrentError { Message = ex.Message, ErrorCode = "EngineError" });

            throw;

        }

    }

    /// Returns true if files were modified and full verification is needed.

    /// </summary>

    internal Task<bool> CheckFilesModifiedInternalAsync(CancellationToken ct) => _fileManager.CheckFilesModifiedAsync(ct);

    private async Task StartOperationsAsync(CancellationToken ct)

    {

        _logger.LogDebug("Starting operations...");

        // === DIAGNOSTIC LOGGING: StartOperationsAsync decision ===
        _logger.LogWarning("[DIAG] StartOperationsAsync: _localBitfield ref={BitfieldRef}, PieceCount={PieceCount}, CompletePieces={Complete}, IsComplete={IsComplete}",
            _localBitfield?.GetHashCode() ?? 0,
            _localBitfield?.PieceCount ?? -1,
            _localBitfield?.CompletePieces ?? -1,
            _localBitfield?.IsComplete ?? false);
        _logger.LogWarning("[DIAG] StartOperationsAsync: _downloadCoordinator null={IsNull}, AreWantedComplete={AreWanted}, HasFilePriorities={HasPriorities}",
            _downloadCoordinator == null,
            _downloadCoordinator?.AreWantedPiecesComplete(_localBitfield) ?? false,
            _downloadCoordinator?.HasFilePriorities ?? false);

        // === LIBTORRENT-STYLE FAST START ===

        // Start download/seed IMMEDIATELY - don't wait for tracker!

        // This is the key optimization: begin operations right away

        // 1. Start downloading or seeding FIRST (before tracker announce)

        // Completion check: use bitfield as authoritative source at startup.
        // _downloadCoordinator.IsWantedComplete uses _wantedHaveCount which may not
        // be synced yet from fast resume data. The bitfield IS synced after Phase 3.
        // libtorrent: is_seed() checks m_picker->is_seeding() (= m_num_have == num_pieces())
        // which is synced during piece checking before files_checked() is called.
        //
        // For selective downloads (file priorities), _localBitfield.IsComplete is false
        // even when all wanted pieces are done. Use AreWantedPiecesComplete for that case.

        var isWantedComplete = _localBitfield.IsComplete
            || (_downloadCoordinator?.AreWantedPiecesComplete(_localBitfield) ?? false);

        _logger.LogWarning("[DIAG] StartOperationsAsync: isWantedComplete={IsWantedComplete}, taking {Branch} branch",
            isWantedComplete, isWantedComplete ? "SEEDING" : "DOWNLOADING");

        if (isWantedComplete)

        {

            StateController?.PostPhase(PhaseTrigger.StartSeeding);

            (_peerManager as PeerManager)?.SetSeeding(true);

            // Release write handles - we only need read access for seeding

            if (_pieceManager != null)
                await _pieceManager.ReleaseWriteHandlesAsync().ConfigureAwait(false);

            _logger.LogInformation("Torrent complete (all wanted pieces), seeding...");

        }

        else

        {

            StateController?.PostPhase(PhaseTrigger.StartDownloading);

            // Gate the download loop on the pause flag. A pause issued during the async
            // startup window sets _transfersPaused (see PauseAsync) but cannot stop a
            // loop that has not started yet — so we simply refrain from starting it.
            // libtorrent parity: files_checked() sets the state to downloading, but
            // peer/transfer activity stays gated behind m_paused.
            if (!IsPaused)
            {
                await _downloadCoordinator.StartAsync(ct).ConfigureAwait(false);

                // Start web seeds (DNS resolution + initial connections)

                if (_webSeedManager != null)
                {
                    await _webSeedManager.StartAsync(ct).ConfigureAwait(false);
                }
            }

        }

        // 2. Start support components (these are fast, non-blocking)

        await _chokingManager.StartAsync(ct).ConfigureAwait(false);

        await _uploadCoordinator.StartAsync(ct).ConfigureAwait(false);

        await _peerProber.StartAsync().ConfigureAwait(false);

        // 3. Start main loop immediately

        _mainTask = Task.Run(() => MainLoopAsync(_stopCts.Token), ct);

        // Honor a pause that arrived during the async startup window. The support
        // components above (choking/upload/prober/main loop) intentionally run even
        // while paused — a normally-paused engine keeps them alive and ResumeAsync
        // assumes they are already running. Here we finish reproducing the canonical
        // paused state: zero rates, disconnect peers, halt announcing, and (defensively)
        // stop the download coordinator in case a pause raced in after the guard above.
        // Without this the engine would announce + connect + download while the UI
        // shows "Paused" (the force-recheck restart reopens this window every time).
        if (IsPaused)
        {
            _torrentStatistics?.SetPaused(true);

            if (_downloadCoordinator != null)
                await _downloadCoordinator.StopAsync().ConfigureAwait(false);

            if (_peerManager != null)
                await _peerManager.StopAsync().ConfigureAwait(false);

            if (_trackerManager != null)
            {
                _trackerManager.PauseAnnouncing();
                _ = AnnouncePausedBestEffortAsync();
            }

            _logger.LogInformation("Torrent reached operations while paused — transfers held (paused during startup)");
            return;
        }

        // 4. FIRE-AND-FORGET tracker announce - don't block startup!

        // Tracker response will be handled via OnPeersDiscovered event

        _ = Task.Run(async () =>

        {

            try

            {

                _logger.LogDebug("Starting async tracker announce...");

                var announceResult = await _trackerManager.AnnounceStartedAsync(BytesRemaining, ct).ConfigureAwait(false);

                if (announceResult.IsSuccess && announceResult.Peers.Count > 0)

                {

                    _logger.LogDebug("Tracker returned {Peers} peers (async)", announceResult.Peers.Count);

                    // Convert to PeerInfo list

                    var peerInfoList = announceResult.Peers.Select(p =>

                        new PeerInfo(p.Ip, p.Port, p.PeerId, "tracker")).ToList();

                    // Feed to adaptive prober

                    _peerProber?.AddCandidatePeers(peerInfoList);

                    // Add peers (this triggers immediate connection attempts)

                    foreach (var peerInfo in peerInfoList)

                    {

                        if (CurrentPhase == TransferPhase.Idle || CurrentPhase == TransferPhase.Stopping)

                            break;

                        await _peerManager.AddPeerAsync(peerInfo, CancellationToken.None).ConfigureAwait(false);

                    }

                    // CONNECT BOOST: Trigger immediate connection attempts like libtorrent

                    DoConnectBoost();

                }

                else if (!announceResult.IsSuccess)

                {

                    _logger.LogWarning("Tracker announce failed: {Error}", string.Join(", ", announceResult.Errors));

                }

            }

            catch (Exception ex)

            {

                _logger.LogWarning(ex, "Async tracker announce failed");

            }

        }, ct);

        _logger.LogDebug("Operations started - download active, tracker announce in progress");

    }

    /// <summary>

    /// Connect boost: immediately attempt connections to available peers.

    /// Follows libtorrent's do_connect_boost() pattern for fast peer acquisition.

    /// </summary>

    private void DoConnectBoost()

    {

        try

        {

            // Trigger peer manager to attempt connections immediately

            // instead of waiting for the next connection cycle

            if (_peerManager is PeerManager pm)

            {

                _logger.LogDebug("Connect boost: triggering immediate peer connections");

                pm.TriggerConnectionAttempts();

            }

        }

        catch (Exception ex)

        {

            _logger.LogWarning(ex, "Connect boost failed");

        }

    }

    /// <summary>

    /// Stops the torrent.

    /// </summary>

    public async Task StopAsync(CancellationToken cancellationToken = default, int stopTrackerTimeoutSeconds = 2)

    {

        if (CurrentPhase == TransferPhase.Idle && !IsPausedState)

            return;

        // A stopped engine is not paused — clear the flag so a stale true can't
        // make a future caller resume a fully-stopped engine.
        _transfersPaused = false;

        _logger.LogInformation("Stopping torrent: {Name}", Name);

        StateController?.PostPhase(PhaseTrigger.Stop);

        _stopCts.Cancel();

        // Dispose web seed manager early (prevents new HTTP requests)

        _webSeedManager?.Dispose();

        _webSeedManager = null;

        // Parallel phase 1: Save peer cache + stop download + disconnect peers

        var phase1Tasks = new List<Task>();

        phase1Tasks.Add(SavePeerCacheAsync());

        if (_downloadCoordinator != null)

            phase1Tasks.Add(_downloadCoordinator.StopAsync());

        if (_peerManager != null)

            phase1Tasks.Add(_peerManager.StopAsync());

        await Task.WhenAll(phase1Tasks).ConfigureAwait(false);

        _dontHaveExtensions.Clear();

        // Fire-and-forget tracker announce (configurable timeout, skip if 0)

        if (stopTrackerTimeoutSeconds > 0 && _trackerManager != null)

        {

            _ = AnnounceStoppedBestEffortAsync(stopTrackerTimeoutSeconds);

        }

        else if (_trackerManager != null)

        {

            try { await _trackerManager.StopAsync().ConfigureAwait(false); }

            catch { /* best effort */ }

        }

        // Parallel phase 2: Stop remaining components (fast — just cancellation)

        var phase2Tasks = new List<Task>();

        if (_uploadCoordinator != null)

            phase2Tasks.Add(_uploadCoordinator.StopAsync());

        if (_chokingManager != null)

            phase2Tasks.Add(_chokingManager.StopAsync());

        if (_peerProber != null)

            phase2Tasks.Add(_peerProber.StopAsync());

        // Stop download verification pipeline (async drain before sync Dispose)
        if (_pieceManager is PieceIO.PieceManager pm && pm.DownloadVerificationPipeline is { } verifyPipeline)
            phase2Tasks.Add(verifyPipeline.StopDownloadVerificationAsync());

        if (phase2Tasks.Count > 0)

            await Task.WhenAll(phase2Tasks).ConfigureAwait(false);

        // Wait for main task

        if (_mainTask != null)

        {

            try

            {

                await _mainTask.WaitAsync(cancellationToken).ConfigureAwait(false);

            }

            catch (OperationCanceledException) { }

        }

        StateController?.PostPhase(PhaseTrigger.Stopped);
        _sendBufferManager?.CancelAll();

        _logger.LogInformation("Torrent stopped");

    }

    /// <summary>

    /// Bounded 'stopped' announce. The token is passed INTO the announce so a dead

    /// tracker releases the single-flight announce lock on timeout (cancelling the

    /// lock wait and per-tracker I/O) instead of holding it for the full retry

    /// schedule. Cancellation does not count toward tracker unavailability.

    /// </summary>

    private async Task AnnounceStoppedBoundedAsync(int timeoutSeconds)

    {

        if (_trackerManager == null)

            return;

        try

        {

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            await _trackerManager.AnnounceStoppedAsync(

                TrackerPayloadUploaded, TrackerPayloadDownloaded, BytesRemaining, cts.Token)

                .ConfigureAwait(false);

        }

        catch (Exception ex)

        {

            _logger.LogDebug(ex, "Bounded stopped announce timed out or failed for {Name}", Name);

        }

    }

    /// <summary>

    /// Engine-stop variant: bounded stopped announce, then the TrackerManager is

    /// stopped (terminal — disposes tracker clients).

    /// </summary>

    private async Task AnnounceStoppedBestEffortAsync(int timeoutSeconds)

    {

        try

        {

            await AnnounceStoppedBoundedAsync(timeoutSeconds).ConfigureAwait(false);

        }

        finally

        {

            try { await _trackerManager.StopAsync().ConfigureAwait(false); }

            catch { /* best effort */ }

        }

    }

    /// <summary>

    /// Pause variant: manager NOT stopped (clients stay alive for the resume-time

    /// 'started' announce); 5s bound.

    /// </summary>

    private Task AnnouncePausedBestEffortAsync() => AnnounceStoppedBoundedAsync(5);

    /// <summary>

    /// Saves the current peer list to cache for fast resume on next start.

    /// Called during shutdown and periodically by the orchestrator's auto-save timer.

    /// </summary>

    public async Task SavePeerCacheAsync()

    {

        if (_peerCache == null || _peerRegistry == null)

            return;

        try

        {

            // Get all peers that aren't banned

            var normalPeers = _peerRegistry.GetPeersWhere(p =>

                p.Status != PeerConnectionStatus.Banned &&

                p.Score != null);

            // Get banned peers (for persistence)

            var bannedPeers = _peerRegistry.GetAllByStatus(PeerConnectionStatus.Banned);

            await _peerCache.SavePeersAsync(

                InfoHashHex,

                normalPeers,

                bannedPeers).ConfigureAwait(false);

            _logger.LogDebug("Saved {NormalCount} peers and {BannedCount} banned peers to cache",

                normalPeers.Count, bannedPeers.Count);

        }

        catch (Exception ex)

        {

            _logger.LogWarning(ex, "Failed to save peer cache");

        }

    }

    /// <summary>

    /// Pauses the torrent (stops downloading but maintains state).

    /// Saves current progress to resume data for persistence.

    /// Follows libtorrent pattern: immediately zeroes rates, stops announcements.

    /// </summary>

    public async Task PauseAsync()

    {

        // Idempotent — a second pause is a no-op.
        if (_transfersPaused)

            return;

        // Nothing to pause on a fully stopped/stopping engine.
        if (CurrentPhase == TransferPhase.Idle || CurrentPhase == TransferPhase.Stopping)

            return;

        // Record the pause intent BEFORE any await. Critical for the startup race:
        // when the engine is still in an early phase (Allocating/CheckingResumeData/
        // CheckingFiles/Connecting) the download loop has not started, so there is
        // nothing to stop yet — but StartOperationsAsync (Phase 7) reads this flag and
        // refrains from starting transfers. The previous phase-gated early-return
        // dropped any pause issued during that window, so the engine downloaded while
        // the UI showed "Paused" (force-recheck reopens this window on every restart).
        // libtorrent parity: pause is the persistent m_paused flag consulted by the
        // transfer/announce logic — not an action contingent on torrent_status::state.
        _transfersPaused = true;

        _logger.LogInformation("Pausing torrent: {Name}", Name);

        // LIBTORRENT PATTERN: Set paused state FIRST so rates immediately show 0

        // This is critical for UI graphs to collapse immediately on pause

        _torrentStatistics?.SetPaused(true);

        // Stop download coordinator (no new requests, cancel pending blocks)

        if (_downloadCoordinator != null)

        {

            await _downloadCoordinator.StopAsync().ConfigureAwait(false);

        }

        // LIBTORRENT IMMEDIATE PAUSE: Disconnect all peers to stop data flow.

        // Peer list is preserved in the registry for fast reconnect on resume.

        if (_peerManager != null)

        {

            await _peerManager.StopAsync().ConfigureAwait(false);

        }

        // libtorrent parity: a paused torrent announces 'stopped' and halts periodic

        // announcing. Without this the announce timer keeps firing while paused, and a

        // dead tracker can hold the single-flight announce lock for minutes — starving

        // the resume-time 'started' announce (observed: 105s, 0/4 trackers).

        if (_trackerManager != null)

        {

            _trackerManager.PauseAnnouncing();

            _ = AnnouncePausedBestEffortAsync();

        }

        // CRITICAL: Save current progress to resume data

        if (_resumeDataProvider != null && _localBitfield != null)

        {

            try

            {

                await _resumeDataProvider.SaveVerifiedPiecesAsync(_localBitfield).ConfigureAwait(false);

                await _resumeDataProvider.UpdateLastActiveTimeAsync(DateTime.UtcNow).ConfigureAwait(false);

                _logger.LogDebug("Saved {Count}/{Total} pieces to resume data on pause",

                    _localBitfield.CompletePieces, _localBitfield.PieceCount);

            }

            catch (Exception ex)

            {

                _logger.LogWarning(ex, "Failed to save resume data on pause");

            }

        }

        // Orthogonal state model: pause flips Intent only — Phase stays
        // Downloading/Seeding (libtorrent parity: paused is a flag, torrent_status::state
        // is unchanged). Resetting the phase here left resume with no legal trigger back
        // to Downloading, so the UI showed "Stopped" while the transfer ran.
        StateController?.PostIntent(IntentTrigger.Pause);

    }

    /// <summary>

    /// Resumes a paused torrent. Skips verification if bitfield is already in memory.

    /// Follows libtorrent pattern: restarts timers, triggers connect boost for fast peer acquisition.

    /// </summary>

    public async Task ResumeAsync(CancellationToken cancellationToken = default)

    {

        // Flag-first with intent fallback (see _transfersPaused). A queued-paused
        // engine has Intent=Queued but stopped transfer loops — it must still resume.
        if (!_transfersPaused && !IsPausedState)

            return;

        _transfersPaused = false;

        if (Interlocked.CompareExchange(ref _resumeInProgress, 1, 0) != 0)

            return;

        _logger.LogInformation("Resuming torrent: {Name}", Name);

        try

        {

            // LIBTORRENT PATTERN: Clear paused state so rates start tracking again

            _torrentStatistics?.SetPaused(false);

            // FAST PATH: If bitfield is already in memory, skip verification entirely

            // This happens when pause/resume occurs in the same session

            if (_localBitfield != null && _localBitfield.PieceCount > 0)

            {

                _logger.LogDebug("Bitfield already in memory ({Count}/{Total} pieces), skipping verification",

                    _localBitfield.CompletePieces, _localBitfield.PieceCount);

                // Restart peer manager (was stopped on pause for libtorrent immediate pause)

                if (_peerManager != null)

                {

                    await _peerManager.StartAsync(cancellationToken).ConfigureAwait(false);

                }

                // Resume download/seeding immediately

                var resumeWantedComplete = _downloadCoordinator?.IsWantedComplete ?? _localBitfield.IsComplete;

                // Orthogonal state model: phase never left Downloading/Seeding during
                // pause, so resume only flips Intent. The conditional Complete/Uncomplete
                // posts reconcile the rare case where file-priority changes while paused
                // moved the wanted set across the completion boundary.

                if (resumeWantedComplete)

                {

                    StateController?.PostIntent(IntentTrigger.Activate);
                    if (CurrentPhase == TransferPhase.Downloading)
                        StateController?.PostPhase(PhaseTrigger.Complete);

                    // Release write handles - we only need read access for seeding

                    if (_pieceManager != null)
                        await _pieceManager.ReleaseWriteHandlesAsync().ConfigureAwait(false);

                }

                else

                {

                    StateController?.PostIntent(IntentTrigger.Activate);
                    if (CurrentPhase == TransferPhase.Seeding)
                        StateController?.PostPhase(PhaseTrigger.Uncomplete);

                    await _downloadCoordinator.StartAsync(cancellationToken).ConfigureAwait(false);

                }

                // Update last active time

                if (_resumeDataProvider != null)

                {

                    await _resumeDataProvider.UpdateLastActiveTimeAsync(DateTime.UtcNow).ConfigureAwait(false);

                }

                // Re-announce to trackers to discover fresh peers (libtorrent sends "started" on resume)

                if (_trackerManager != null)

                {

                    _trackerManager.ResumeAnnouncing();

                    _ = _trackerManager.AnnounceStartedAsync(BytesRemaining, cancellationToken);

                }

                // LIBTORRENT PATTERN: Connect boost for fast peer acquisition on resume

                // This accelerates reconnection to previously known peers

                DoConnectBoost();

                _logger.LogDebug("Torrent resumed instantly (same session)");

                return;

            }

            // SLOW PATH: Need to reload bitfield from resume data.
            // Phase still reflects the pre-pause lifecycle stage (orthogonal model) —
            // only the intent flips back to Active here.

            StateController?.PostIntent(IntentTrigger.Activate);

            // Try to load from resume data first

            if (_resumeDataProvider != null)

            {

                var savedBitfield = await _resumeDataProvider.LoadHavePiecesAsync().ConfigureAwait(false);

                if (savedBitfield != null)

                {

                    _localBitfield = savedBitfield;

                    _logger.LogDebug("Loaded {Count}/{Total} pieces from resume data",

                        _localBitfield.CompletePieces, _localBitfield.PieceCount);

                }

                else

                {

                    // No saved bitfield - need full verification

                    _logger.LogWarning("No saved bitfield, running full verification");

                    var verificationResult = await _fileManager.VerifyIntegrityOnResumeAsync(cancellationToken).ConfigureAwait(false);

                    if (verificationResult.CorruptPieces.Count > 0)

                    {

                        _logger.LogWarning("Found {Count} corrupt pieces", verificationResult.CorruptPieces.Count);

                    }

                }

            }

            // Resume download/seeding (use wanted-based completion for selective download).
            // Phase is already Downloading/Seeding; only reconcile if the wanted set
            // crossed the completion boundary while paused (file-priority changes).

            if (_downloadCoordinator.IsWantedComplete)

            {

                if (CurrentPhase == TransferPhase.Downloading)
                    StateController?.PostPhase(PhaseTrigger.Complete);

                // Release write handles - we only need read access for seeding

                if (_pieceManager != null)
                    await _pieceManager.ReleaseWriteHandlesAsync().ConfigureAwait(false);

            }

            else

            {

                if (CurrentPhase == TransferPhase.Seeding)
                    StateController?.PostPhase(PhaseTrigger.Uncomplete);

                await _downloadCoordinator.StartAsync(cancellationToken).ConfigureAwait(false);

            }

            // Update last active time

            if (_resumeDataProvider != null)

            {

                await _resumeDataProvider.UpdateLastActiveTimeAsync(DateTime.UtcNow).ConfigureAwait(false);

            }

            // Re-announce to trackers (fast path already does this; the slow path

            // previously never announced, leaving peer discovery to DHT alone).

            if (_trackerManager != null)

            {

                _trackerManager.ResumeAnnouncing();

                _ = _trackerManager.AnnounceStartedAsync(BytesRemaining, cancellationToken);

            }

            // LIBTORRENT PATTERN: Connect boost for fast peer acquisition on resume

            DoConnectBoost();

            _logger.LogInformation("Torrent resumed successfully");

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Error resuming torrent");

            StateController?.PostError(new TorrentError { Message = ex.Message, ErrorCode = "EngineError" });

            throw;

        }

        finally

        {

            Interlocked.Exchange(ref _resumeInProgress, 0);

        }

    }

    #region Move Storage (libtorrent-style)

    /// <summary>

    /// Moves torrent storage to a new location while maintaining peer connections.

    /// Implements libtorrent-style move_storage with disk fence.

    /// </summary>

    /// <param name="newPath">The new save path for the torrent.</param>

    /// <param name="ct">Cancellation token.</param>

    /// <returns>Result of the move operation.</returns>

    public async Task<MoveStorageResult> MoveStorageAsync(string newPath, CancellationToken ct = default)

    {

        if (CurrentPhase == TransferPhase.Idle && !IsPausedState)

            return MoveStorageResult.Failed("Engine must be running to move storage");

        if (string.IsNullOrWhiteSpace(newPath))

            return MoveStorageResult.Failed("New path cannot be empty");

        if (string.Equals(_downloadPath, newPath, StringComparison.OrdinalIgnoreCase))

            return MoveStorageResult.Success(newPath); // Already at this path

        var oldPath = _downloadPath;

        _logger.LogInformation("Moving storage from {OldPath} to {NewPath}", oldPath, newPath);

        try

        {

            // 1. Set state to Moving (peers stay connected)

            StateController?.PostFileOp(FileOpTrigger.StartMove);

            // 2. Raise disk fence in PieceManager

            //    - Blocks new disk I/O

            //    - Drains pending write queue

            //    - Closes all file handles

            var fenceTimeout = TimeSpan.FromSeconds(30);

            var fenceRaised = await _pieceManager.RaiseDiskFenceAsync(fenceTimeout, ct).ConfigureAwait(false);

            if (!fenceRaised)

            {

                _logger.LogWarning("Failed to raise disk fence within timeout");

                _pieceManager.LowerDiskFence();

                StateController?.PostFileOp(FileOpTrigger.Finish);

                return MoveStorageResult.Failed("Timeout waiting for disk operations to complete");

            }

            _logger.LogDebug("Disk fence raised, moving files...");

            // 3. Move files

            var moveSuccess = await _fileManager.MoveFilesInternalAsync(oldPath, newPath, ct).ConfigureAwait(false);

            if (!moveSuccess.success)

            {

                // Rollback: lower fence, stay at old path

                _pieceManager.LowerDiskFence();

                StateController?.PostFileOp(FileOpTrigger.Finish);

                return MoveStorageResult.Failed(moveSuccess.error);

            }

            // 4. Update internal paths

            _downloadPath = newPath;

            _pieceManager.UpdateBasePath(newPath);

            // Update partfile wrapper with new base path
            if (_diskBackend is PartFileAwareDiskBackend partFileBackend)
                partFileBackend.UpdateBasePath(newPath);

            // 5. Lower fence - I/O resumes

            _pieceManager.LowerDiskFence();

            // 6. Restore previous state

            StateController?.PostFileOp(FileOpTrigger.Finish);

            _logger.LogInformation("Storage moved successfully to {NewPath}", newPath);

            return MoveStorageResult.Success(newPath, moveSuccess.needsRecheck);

        }

        catch (OperationCanceledException)

        {

            _pieceManager?.LowerDiskFence();

            StateController?.PostFileOp(FileOpTrigger.Finish);

            throw;

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Error during move_storage operation");

            _pieceManager?.LowerDiskFence();

            // Restore previous state so torrent can continue operating from original location

            // (libtorrent behavior: move failure doesn't stop the torrent)

            StateController?.PostFileOp(FileOpTrigger.Finish);

            return MoveStorageResult.Failed($"Move failed: {ex.Message}", ex);

        }

    }

    /// <summary>

    /// Gets the current download path.

    /// </summary>

    public string DownloadPath => _downloadPath;

    #endregion

    /// <summary>

    /// Verify file integrity of downloaded pieces.

    /// Called automatically during resume, can also be called manually.

    /// </summary>

    public async Task<VerificationResult> VerifyIntegrityAsync(
        VerificationOptions options = null,
        IProgress<VerificationProgress> progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _fileManager.VerifyIntegrityAsync(options, progress, cancellationToken).ConfigureAwait(false);

        // Raise event (stays in TorrentEngine as it owns the event)
        if (!result.Cancelled && result.Error == null)
        {
            IntegrityVerificationCompleted?.Invoke(this, new IntegrityVerificationEventArgs(result));
        }

        return result;
    }

    private async Task MainLoopAsync(CancellationToken cancellationToken)

    {

        var announceInterval = TimeSpan.FromSeconds(TrackerCommunication.TrackerConstants.DefaultAnnounceInterval);

        var lastAnnounce = DateTime.UtcNow;

        var seederSwarmCheckInterval = TimeSpan.FromSeconds(15);

        var lastSeederSwarmCheck = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested)

        {

            try

            {

                // Periodic seeder swarm detection

                if (DateTime.UtcNow - lastSeederSwarmCheck > seederSwarmCheckInterval)

                {

                    UpdateSeederSwarmStatistics();

                    lastSeederSwarmCheck = DateTime.UtcNow;

                }

                // Periodic tracker announce

                if (DateTime.UtcNow - lastAnnounce > announceInterval)

                {

                    var result = await _trackerManager.AnnounceRegularAsync(

                        TrackerPayloadUploaded, TrackerPayloadDownloaded, BytesRemaining, cancellationToken).ConfigureAwait(false);

                    if (result.IsSuccess)

                    {

                        // Convert to PeerInfo list

                        var peerInfoList = result.Peers.Select(p =>

                            new PeerInfo(p.Ip, p.Port, p.PeerId, "tracker")).ToList();

                        // Feed candidate peers to adaptive prober

                        _peerProber?.AddCandidatePeers(peerInfoList);

                        // Add new peers to peer manager (batched)

                        await _peerManager.AddPeersAsync(peerInfoList, cancellationToken).ConfigureAwait(false);

                        // Update interval from tracker response

                        if (result.RecommendedInterval > 0)

                        {

                            announceInterval = TimeSpan.FromSeconds(

                                Math.Max(result.RecommendedInterval, _trackerMonitor.CurrentValue.MinAnnounceInterval));

                        }

                    }

                    lastAnnounce = DateTime.UtcNow;

                }

                // BEP 55: clean up expired holepunch cooldowns
                _holepunchManager?.CleanupExpiredCooldowns();

                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

            }

            catch (OperationCanceledException)

            {

                break;

            }

            catch (Exception ex)

            {

                _logger.LogError(ex, "Error in main loop");

                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);

            }

        }

    }

    internal void OnPeersDiscoveredInternal(object sender, PeersDiscoveredEventArgs e) => OnPeersDiscovered(sender, e);

    private void OnPeersDiscovered(object sender, PeersDiscoveredEventArgs e)

    {

        _logger.LogDebug("Discovered {Count} peers from {Tracker}",

            e.Peers.Count, e.TrackerUrl);

        // Forward to our event

        PeersDiscovered?.Invoke(this, e);

        // Convert to PeerInfo list for prober

        var peerInfoList = e.Peers.Select(p => new PeerInfo(p.Ip, p.Port, p.PeerId, "tracker")).ToList();

        // Feed candidate peers to adaptive prober

        _peerProber?.AddCandidatePeers(peerInfoList);

        // Add to peer manager

        foreach (var peerInfo in peerInfoList)

        {

            _ = _peerManager.AddPeerAsync(peerInfo);

        }

    }

    internal void OnPieceCompletedInternal(object sender, PieceCompletedEventArgs e) => OnPieceCompleted(sender, e);

    private void OnPieceCompleted(object sender, PieceCompletedEventArgs e)

    {

        _logger.LogDebug("Piece {Index} completed ({Completed}/{Total})",

            e.PieceIndex, e.CompletedPieces, e.TotalPieces);

        // Update per-file progress

        _fileProgressTracker?.OnPieceCompleted(e.PieceIndex);

        ReportProgress();

    }

    private void OnProgressChanged(object sender, DownloadProgressEventArgs e)

    {

        ReportProgress();

    }

    internal void OnDiskWriteErrorInternal(object sender, DiskErrorEventArgs e) => OnDiskWriteError(sender, e);

    private void OnDiskWriteError(object sender, DiskErrorEventArgs e)

    {

        _logger.LogError("Disk write error — entering error state (piece {Piece}: {Error})",

            e.PieceIndex, e.ErrorMessage);

        // Stop download coordinator to prevent further write attempts.

        // libtorrent enters "upload mode" on disk errors; we stop downloading entirely.

        // The torrent must be manually resumed after freeing disk space.

        _ = _downloadCoordinator?.StopAsync();

        StateController?.PostError(new TorrentError { Message = $"Disk write error — piece {e.PieceIndex}: {e.ErrorMessage}", ErrorCode = "EngineError" });

    }

    /// <summary>
    /// libtorrent parity: files_checked() → is_seed() → finished() → completed().
    /// Called after background verification completes to check if all wanted pieces
    /// are now verified, transitioning from Downloading to Seeding if so.
    ///
    /// Must stop download components before transitioning — the download loop's
    /// IsWantedComplete check uses _wantedHaveCount which is NOT updated by
    /// background verification (only by the download path).
    /// </summary>
    internal async Task EvaluateCompletionAfterVerification()
    {
        // Guard: don't transition if engine is stopping/stopped/disposed
        if (_disposed || CurrentPhase == TransferPhase.Stopping || CurrentPhase == TransferPhase.Idle)
            return;

        // Determine completion from the actual bitfield, not the download coordinator's
        // cached _wantedHaveCount which isn't updated by background verification.
        bool isComplete;
        if (_downloadCoordinator != null && _downloadCoordinator.HasFilePriorities)
        {
            isComplete = _downloadCoordinator.AreWantedPiecesComplete(_localBitfield);
        }
        else
        {
            isComplete = _localBitfield.IsComplete;
        }

        // Volatile read: ensure we see the latest _phase written by another thread.
        // Volatile.Read<T> requires a reference type; TransferPhase is an enum, so use a barrier.
        Thread.MemoryBarrier();

        _logger.LogWarning("[DIAG] EvaluateCompletionAfterVerification: isComplete={IsComplete}, phase={Phase}, willTransition={Will}",
            isComplete, CurrentPhase, isComplete && CurrentPhase == TransferPhase.Downloading);

        if (!isComplete || CurrentPhase != TransferPhase.Downloading)
            return;

        _logger.LogInformation("Verification complete — all wanted pieces present, switching to seeding");

        // Stop download components BEFORE state transition.
        // DownloadCoordinator.StopAsync cancels the download loop and PipelineTick.
        if (_downloadCoordinator != null)
            await _downloadCoordinator.StopAsync().ConfigureAwait(false);

        // WebSeedManager uses Dispose (no async stop)
        _webSeedManager?.Dispose();
        _webSeedManager = null;

        StateController?.PostPhase(PhaseTrigger.Complete);
        StateController?.PostMetrics(isFinished: true, isSeed: _localBitfield?.IsComplete ?? false);
        (_peerManager as PeerManager)?.SetSeeding(true);

        // Release write handles — only need read access for seeding
        if (_pieceManager != null)
            await _pieceManager.ReleaseWriteHandlesAsync().ConfigureAwait(false);

        // Announce completed to tracker (like libtorrent's completed())
        _ = _trackerManager?.AnnounceCompletedAsync(TrackerPayloadUploaded, TrackerPayloadDownloaded);

        // Fire event so orchestrator sets managed.IsFinished, CompletedTime, notifications
        DownloadCompleted?.Invoke(this, EventArgs.Empty);
    }

    internal void OnDownloadCompletedInternal(object sender, EventArgs e) => OnDownloadCompleted(sender, e);

    private async void OnDownloadCompleted(object sender, EventArgs e)

    {

        // FIX: Set state BEFORE logging to ensure consistent state when events propagate.

        // This prevents race conditions where the UI sees "Download completed" in logs

        // but the state is still Downloading.

        StateController?.PostPhase(PhaseTrigger.Complete);

        _logger.LogInformation("Download completed for {Name}! Now seeding.", Name);

        // Release write handles - we only need read access for seeding

        // This allows external programs to execute downloaded files (especially .exe files)

        if (_pieceManager != null)
            await _pieceManager.ReleaseWriteHandlesAsync().ConfigureAwait(false);

        // Enable seeding mode in PeerManager for redundant connection handling

        (_peerManager as PeerManager)?.SetSeeding(true);

        // Announce completed to tracker

        _ = _trackerManager.AnnounceCompletedAsync(TrackerPayloadUploaded, TrackerPayloadDownloaded);

        DownloadCompleted?.Invoke(this, EventArgs.Empty);

    }

    internal Action<bool> OnSeederSwarmStateChangedInternal => OnSeederSwarmStateChanged;

    private void OnSeederSwarmStateChanged(bool isSeederSwarm)

    {

        if (isSeederSwarm)

        {

            _logger.LogDebug("Entering seeder swarm mode - enabling optimizations");

            // Enable auto-sequential mode for faster downloads in seeder swarms

            _downloadCoordinator?.SetAutoSequentialMode(true);

            _torrentStatistics.AutoSequentialActive = true;

        }

        else

        {

            _logger.LogDebug("Leaving seeder swarm mode");

            _downloadCoordinator?.SetAutoSequentialMode(false);

            _torrentStatistics.AutoSequentialActive = false;

        }

    }

    private void UpdateSeederSwarmStatistics()

    {

        if (_torrentStatistics == null)

            return;

        // Update peer counts for seeder swarm detection

        _torrentStatistics.ConnectedPeers = ConnectedPeers;

        _torrentStatistics.ConnectedSeeds = ConnectedSeeds;

        _torrentStatistics.TrackerSeeders = TotalSeeders;

        _torrentStatistics.TrackerLeechers = TotalLeechers;

        _torrentStatistics.PiecesCompleted = PiecesCompleted;

        // Use FileProgressTracker for consistent wanted-bytes calculation.

        // Previously used (Progress * TotalSize) which is bitfield-based and doesn't

        // account for file priorities — causing oscillation as BackgroundTaskManager

        // sets the same field from FileProgressTracker with a different value.

        _torrentStatistics.TotalWantedDone = _fileProgressTracker?.GetWantedBytesCompleted()

            ?? (long)(Progress * TotalSize);

        _torrentStatistics.TotalWanted = _fileProgressTracker?.GetTotalWantedBytes()

            ?? TotalSize;

        _torrentStatistics.DownloadRate = (int)DownloadRate;

        _torrentStatistics.UploadRate = (int)UploadRate;

        // Trigger seeder swarm detection

        _seederSwarmDetector?.Update();

    }

    // SetPhase, SetEngineError, SetPaused, SetMoving, EndMove, OnStateChanged removed.
    // All state mutations now go through StateController (Task 7 refactor).

    private void ReportProgress()

    {

        ProgressChanged?.Invoke(this, new TorrentProgressEventArgs(

            PiecesCompleted,

            PieceCount,

            BytesDownloaded,

            VerifiedDownloaded,  // Add verified bytes for accurate progress tracking

            BytesInProgress,

            TotalUploaded,

            TotalSize,

            DownloadRate,

            UploadRate,

            ConnectedPeers,

            ConnectedSeeds,

            UnchokedPeers,

            TotalSeeders,

            TotalLeechers,

            _downloadCoordinator?.PendingRequests ?? 0,

            _downloadCoordinator?.InProgressPieces ?? 0,

            _torrentStatistics?.FailedBytes ?? 0));

    }

    /// <summary>

    /// Update file availability from connected peer bitfields.

    /// Call this periodically to refresh availability data.

    /// </summary>

    public void UpdateFileAvailability()

    {

        if (_fileProgressTracker == null || _peerManager == null)

            return;

        try

        {

            var peerBitfields = _peerManager.ConnectedPeers

                .Where(p => p.PeerBitfield != null)

                .Select(p => p.PeerBitfield);

            _fileProgressTracker.UpdateAvailability(peerBitfields);

        }

        catch (Exception ex)

        {

            _logger.LogDebug(ex, "Error updating file availability");

        }

    }

    #region Selective Download (File Priority)

    /// <summary>
    /// Set priority for a specific file.
    /// </summary>
    /// <param name="fileIndex">Index of the file in the torrent</param>
    /// <param name="priority">Priority level (0=skip, 1-7=priority levels)</param>
    public void SetFilePriority(int fileIndex, FilePriority priority)
        => _settingsApplier.SetFilePriority(fileIndex, priority);

    /// <summary>
    /// Set all file priorities from an array (one entry per file).
    /// Also updates the DownloadCoordinator's piece picker and statistics.
    /// </summary>
    public void SetAllFilePriorities(FilePriority[] priorities)
        => _settingsApplier.SetAllFilePriorities(priorities);

    /// <summary>
    /// Set priority for multiple files.
    /// </summary>
    public void SetFilePriorities(IEnumerable<(int fileIndex, FilePriority priority)> priorities)
        => _settingsApplier.SetFilePriorities(priorities);

    /// <summary>

    /// Get priority for a specific file.

    /// </summary>

    public FilePriority GetFilePriority(int fileIndex)

    {

        if (_fileProgressTracker == null)

            return FilePriority.Normal;

        var file = _fileProgressTracker.GetFileProgress(fileIndex);

        return (FilePriority)file.Priority;

    }

    /// <summary>

    /// Get priorities for all files.

    /// </summary>

    public IReadOnlyList<FilePriority> GetAllFilePriorities()

    {

        if (_fileProgressTracker == null)

            return Array.Empty<FilePriority>();

        return _fileProgressTracker.Files

            .Select(f => (FilePriority)f.Priority)

            .ToList();

    }

    /// <summary>

    /// Check if a piece is wanted based on file priorities.

    /// </summary>

    public bool IsPieceWanted(int pieceIndex)

    {

        return _fileProgressTracker?.IsPieceWanted(pieceIndex) ?? true;

    }

    #endregion

    /// <summary>
    /// Set the resume data provider for fast resume functionality
    /// </summary>
    public void SetResumeDataProvider(IResumeDataProvider provider)
    {
        _resumeDataProvider = provider;
    }

    /// <summary>

    /// Gets current statistics for the torrent.

    /// </summary>

    public TorrentStats GetStats()

    {

        return new TorrentStats

        {

            Phase = Phase,

            Progress = Progress,

            PiecesCompleted = PiecesCompleted,

            TotalPieces = PieceCount,

            BytesDownloaded = BytesDownloaded,

            BytesUploaded = BytesUploaded,

            TotalSize = TotalSize,

            BytesRemaining = BytesRemaining,

            DownloadRate = DownloadRate,

            ConnectedPeers = ConnectedPeers,

            TotalSeeders = TotalSeeders,

            TotalLeechers = TotalLeechers,

            StartTime = _startTime,

            ElapsedTime = DateTime.UtcNow - _startTime

        };

    }

    /// <summary>
    /// Toggle BEP 16 super-seeding mode.
    /// </summary>
    public async Task SetSuperSeedingAsync(bool enabled)
    {
        if (_superSeedManager == null) return;

        if (enabled)
        {
            _peerManager.SuperSeedingActive = true;
            _superSeedManager.Enable();
        }
        else
        {
            _peerManager.SuperSeedingActive = false;
            await _superSeedManager.DisableAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Exit seed mode after hash verification failure.
    /// Disconnects all peers (no protocol mechanism to retract HAVE messages)
    /// and triggers background recheck of unverified pieces.
    /// libtorrent parity: leave_seed_mode() in torrent.cpp:494-530.
    /// </summary>
    internal async Task ExitSeedModeAsync()
    {
        if (!IsSeedMode) return;

        _logger.LogWarning("Exiting seed mode — piece failed hash verification");

        IsSeedMode = false;

        // Disconnect all peers — they received all-ones bitfield which is now invalid
        // libtorrent parity: leave_seed_mode calls disconnect_all()
        var peers = _peerManager.ConnectedPeers.ToList();
        _logger.LogInformation("Disconnecting {Count} peers due to seed mode exit", peers.Count);
        var tasks = peers.Select(p => p.DisconnectAsync());
        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Clear the verifier from upload path
        _uploadCoordinator?.SetSeedModeVerifier(null);
        _seedModeVerifier = null;

        // Transition to verifying state
        StateController?.PostPhase(PhaseTrigger.CheckFiles);

        // Start background verification of unverified pieces
        // Intentional deviation from libtorrent: we only recheck unverified pieces
        _phaseInitializer.StartBackgroundVerification(CancellationToken.None);

        _logger.LogInformation("Seed mode exit complete — rechecking unverified pieces");
    }

    #region DHT Integration

    /// <summary>

    /// Add a peer discovered via DHT to the peer manager.

    /// </summary>

    public async Task AddPeerFromDhtAsync(PeerInfo peer)

    {

        if (_peerManager == null || peer == null)

            return;

        // BEP 27: private torrents must not accept non-tracker peers

        if (_torrent.Info.IsPrivate)

        {

            _logger.LogDebug("Rejecting DHT/LPD peer {Peer} — private torrent", peer.EndPoint);

            return;

        }

        try

        {

            // Set source to DHT for tracking

            var dhtPeer = new PeerInfo(peer.IpAddress, peer.Port, peer.PeerId, "dht");

            // Feed to adaptive prober for trial-based selection

            _peerProber?.AddCandidatePeers(new List<PeerInfo> { dhtPeer });

            // Add to peer manager

            await _peerManager.AddPeerAsync(dhtPeer).ConfigureAwait(false);

            _logger.LogDebug("Added DHT peer: {Peer}", peer);

        }

        catch (Exception ex)

        {

            _logger.LogDebug(ex, "Failed to add DHT peer: {Peer}", peer);

        }

    }

    #endregion

    public void Dispose()

    {

        if (_disposed)

            return;

        _disposed = true;

        _stopCts.Cancel();

        _downloadCoordinator?.Dispose();

        _uploadCoordinator?.Dispose();

        _sendBufferManager?.Dispose();
        _sendBufferManager = null;

        _chokingManager?.Dispose();

        _trackerManager?.Dispose();

        _peerManager?.Dispose();

        (_pieceManager as IDisposable)?.Dispose();

        DiskWriteThrottlerInternal?.Dispose();
        DiskWriteThrottlerInternal = null;

        if (_diskBackend != null)
        {
            _diskBackend.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _diskBackend = null;
        }

        _peerCache?.Dispose();

        // BEP 55: dispose holepunch manager
        _holepunchManager?.Dispose();
        _holepunchManager = null;

        _stopCts.Dispose();

        _logger.LogDebug("TorrentEngine disposed");

    }

}