using System;

using System.Collections.Generic;

using System.IO;

using System.Linq;

using System.Threading;

using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using vTorrent.Bencode.Objects;

using vTorrent.Bencode.Parsers;

using vTorrent.Bencode.Torrents;

using vTorrent.Core.Orchestration.Alerts;

using vTorrent.Core.Persistence;

using vTorrent.Core.ResumeData;

using vTorrent.Core.Session;

using vTorrent.Storage;

using vTorrent.Abstractions.Settings;

using vTorrent.Core.PeerCommunication.Models;


using vTorrent.Core.DHT;

using vTorrent.Core.Network;

using vTorrent.Core.Orchestration.Bandwidth;

using vTorrent.Core.PeerCommunication.Transport;

using vTorrent.Core.PeerCommunication.Transport.Tcp;

using vTorrent.Core.PieceIO;

using System.Collections;

using System.Collections.Concurrent;

using vTorrent.Core;

using vTorrent.Core.Events;

using vTorrent.Core.State;

using vTorrent.Abstractions.Enums;

using vTorrent.Abstractions.Interfaces.Storage;

using vTorrent.Abstractions.Interfaces.Transport;

using vTorrent.Abstractions.Models;

using vTorrent.Abstractions.Records;

using vTorrent.Abstractions.Settings;
using vTorrent.Core.Engine;
using vTorrent.Abstractions.Interfaces;
using vTorrent.Core.Network.Proxy;
using vTorrent.Core.Network.I2P;
using vTorrent.Core.IO;
using vTorrent.Core.Settings;
using vTorrent.Core.TrackerCommunication.Http;
using vTorrent.Core.TrackerCommunication.Udp;

namespace vTorrent.Core.Orchestration;

/// <summary>

/// Central torrent orchestrator that manages all running torrents.

/// Handles runtime state, resource allocation, and coordinates with persistence layer.

/// Similar to libtorrent's session_impl.

/// </summary>

public class TorrentOrchestrator : IAsyncDisposable, IVpnStatusService

{

    #region Fields

    private readonly SessionPersistence _persistence;

    private readonly ILoggerFactory _loggerFactory;

    private readonly ILogger<TorrentOrchestrator> _logger;

    private readonly ISecureFileWiper _secureFileWiper;

    private readonly DeletionWorker _deletionWorker;

    // Runtime collections

    private readonly TorrentCollection _torrents;

    private readonly StateIndex _stateIndex;

    private readonly QueueManager _queue;

    // Auto-management

    private readonly AutoManager _autoManager;

    // Limits concurrent engine starts to prevent disk/CPU thrashing (thundering herd)
    private readonly SemaphoreSlim _engineStartGate;

    private int _embeddedRestoreCount;
    private int _diskFallbackRestoreCount;

    // Seeding limit enforcement

    private readonly SeedingLimitEnforcer _seedingLimitEnforcer;

    // Resource management

    private readonly ResourceAllocator _resourceAllocator;

    private readonly AlertManager _alertManager;

    private readonly EngineFactory _engineFactory;

    // Bandwidth management

    private readonly GlobalBandwidthCoordinator _bandwidthCoordinator;

    // Statistics

    private readonly SessionStatistics _statistics;

    private SessionState? _sessionState;

    // Metadata download coordinators for magnet links

    private readonly Dictionary<string, MetadataDownloadCoordinator> _metadataCoordinators = new();

    private readonly object _metadataCoordinatorLock = new();

    // State

    private bool _isInitialized;

    private bool _isShuttingDown;

    private DateTime _sessionStartTime;

    // Decomposed managers (Phase 5 god class decomposition)

    private readonly TorrentLifecycleManager _lifecycleManager;

    private readonly DhtCoordinator _dhtCoordinator;

    private readonly BackgroundTaskManager _backgroundTaskManager;

    // Shared UDP socket for uTP + DHT (null when not using uTP)

    private readonly UdpSocketManager? _udpSocketManager;

    private readonly UdpTrackerPacketHandler? _trackerPacketHandler;

    // Session-level uTP socket manager (outbound half); shared with the session TransportConnector
    // and registered on the shared UDP socket once it is bound.
    private PeerCommunication.Transport.Utp.UtpSocketManager? _utpSocketManager;

    // Transport connector shared across all engines (uTP-first or TCP-only)

    private readonly ITransportConnector _transportConnector;

    // Persistence queue for batching dirty-status writes (wired later)

    private PersistenceQueue? _persistenceQueue;

    // Session-level inbound peer connection dispatcher (TCP listener + handshake routing)
    private IncomingConnectionDispatcher? _incomingDispatcher;

    // BEP 24: External IP voter for multi-source consensus
    private readonly ExternalIpVoter _externalIpVoter = new();

    // Proxy connector for peer/tracker connections (null when no proxy configured)
    private readonly IProxyConnector? _proxyConnector;

    // VPN kill-switch (null when not enabled)
    private VpnKillSwitch? _vpnKillSwitch;

    // Shared IP filter (populated during InitializeAsync from file + session state)
    private readonly vTorrent.Core.Network.IpFilter.IpFilter _ipFilter = new();

    // Peer class manager for IP-based bandwidth classification
    private readonly Network.PeerClass.PeerClassManager? _peerClassManager;

    // Profile scheduler for time-based profile/mode switching
    private ProfileScheduler? _scheduler;

    // Seed connect injection counter (ConnectSeedEveryNDownload)
    private int _downloadConnectAttempts;
    private readonly IOptionsMonitor<QueueSettings>? _queueMonitor;
    private readonly IOptionsMonitor<TrackerSettings>? _trackerMonitor;
    private readonly IOptionsMonitor<ConnectionSettings>? _connectionMonitor;
    private readonly IOptionsMonitor<DhtSettings>? _dhtMonitor;
    private readonly IOptionsMonitor<EncryptionSettings>? _encryptionMonitor;
    private readonly IOptionsMonitor<WebSeedSettings>? _webSeedMonitor;
    private readonly IOptionsMonitor<ProxySettings>? _proxyMonitor;
    private readonly IOptionsMonitor<VpnSettings>? _vpnMonitor;
    private readonly IOptionsMonitor<AutoSaveSettings>? _autoSaveMonitor;
    private readonly IOptionsMonitor<ProtocolSettings>? _protocolMonitor;
    private readonly IOptionsMonitor<BehaviorSettings>? _behaviorMonitor;
    private readonly IOptionsMonitor<PeerSettings>? _peerMonitor;
    private readonly IOptionsMonitor<DiskSettings>? _diskMonitor;
    private readonly IOptionsMonitor<I2pSettings>? _i2pMonitor;
    private I2pService? _i2pService;
    private readonly IDisposable? _behaviorChangeRegistration;
    private readonly IDisposable? _scheduleChangeRegistration;
    private IDisposable? _vpnChangeRegistration;
    private IDisposable? _i2pChangeRegistration;

    // Disk I/O subsystem: session-level space monitor and error recovery
    private readonly DiskSpaceMonitor _diskSpaceMonitor;
    private readonly DiskErrorRecoveryManager _diskErrorRecoveryManager;

    #endregion

    #region Internal Accessors (for decomposed managers)

    internal TorrentCollection TorrentsInternal => _torrents;

    internal ILoggerFactory LoggerFactoryInternal => _loggerFactory;

    internal UdpSocketManager? UdpSocketManagerInternal => _udpSocketManager;

    internal ITransportConnector TransportConnectorInternal => _transportConnector;

    internal ConcurrentDictionary<string, ConcurrentQueue<PeerInfo>> PendingDhtPeers => _dhtCoordinator.PendingDhtPeers;

    internal DiskSpaceMonitor DiskSpaceMonitorInternal => _diskSpaceMonitor;
    internal DiskErrorRecoveryManager DiskErrorRecoveryManagerInternal => _diskErrorRecoveryManager;

    internal IOptionsMonitor<ConnectionSettings>? ConnectionMonitorInternal => _connectionMonitor;
    internal IOptionsMonitor<AutoSaveSettings>? AutoSaveMonitorInternal => _autoSaveMonitor;
    internal IOptionsMonitor<VpnSettings>? VpnMonitorInternal => _vpnMonitor;
    internal IOptionsMonitor<TrackerSettings>? TrackerMonitorInternal => _trackerMonitor;
    internal IOptionsMonitor<BehaviorSettings>? BehaviorMonitorInternal => _behaviorMonitor;

    /// <summary>
    /// Exposes the active ProfileScheduler instance so it can be passed to the web server DI container.
    /// Null until the orchestrator is initialized with a ProfileManager.
    /// </summary>
    public ProfileScheduler? Scheduler => _scheduler;
    internal IOptionsMonitor<PeerSettings>? PeerMonitorInternal => _peerMonitor;
    internal IOptionsMonitor<DiskSettings>? DiskMonitorInternal => _diskMonitor;
    internal IOptionsMonitor<I2pSettings>? I2pMonitorInternal => _i2pMonitor;

    internal Dictionary<string, MetadataDownloadCoordinator> MetadataCoordinators => _metadataCoordinators;

    internal object MetadataCoordinatorLock => _metadataCoordinatorLock;

    #endregion

    #region Properties

    /// <summary>

    /// Session-wide statistics

    /// </summary>

    public SessionStatistics Statistics => _statistics;

    /// <summary>
    /// BEP 24: External IP voter for multi-source consensus
    /// </summary>
    public IExternalIpVoter ExternalIpVoter => _externalIpVoter;

    /// <summary>

    /// Global settings (from persistence layer)

    /// </summary>

    public GlobalSettings Settings => _persistence.Settings;

    /// <summary>

    /// Whether orchestrator is initialized

    /// </summary>

    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Injects the ITorrentService into the ProfileScheduler after construction
    /// to avoid a circular dependency between TorrentOrchestrator and TorrentService.
    /// Called by TorrentService once it has been fully constructed.
    /// </summary>
    internal void InjectTorrentService(vTorrent.Abstractions.Interfaces.Services.ITorrentService torrentService)
    {
        _scheduler?.InjectTorrentService(torrentService);
    }

    /// <summary>

    /// Whether orchestrator is shutting down

    /// </summary>

    public bool IsShuttingDown => _isShuttingDown;

    /// <summary>

    /// Number of managed torrents

    /// </summary>

    public int TorrentCount => _torrents.Count;

    /// <summary>

    /// Session uptime

    /// </summary>

    public TimeSpan Uptime => DateTime.UtcNow - _sessionStartTime;

    /// <summary>

    /// Auto-manager instance (for configuration)

    /// </summary>

    public AutoManager AutoManager => _autoManager;

    /// <summary>

    /// Queue manager instance (for queue operations)

    /// </summary>

    public QueueManager QueueManager => _queue;

    /// <summary>

    /// State index (for state-based queries)

    /// </summary>

    public StateIndex StateIndex => _stateIndex;

    /// <summary>

    /// Alert manager for subscribing to session events

    /// </summary>

    public AlertManager Alerts => _alertManager;

    /// <summary>

    /// Resource allocator for monitoring connection/bandwidth usage

    /// </summary>

    public ResourceAllocator Resources => _resourceAllocator;

    /// <summary>

    /// Persistence layer for database operations

    /// </summary>

    public SessionPersistence Persistence => _persistence;

    /// <summary>

    /// DHT coordinator for distributed peer discovery and LPD

    /// </summary>

    internal DhtCoordinator DhtCoordinator => _dhtCoordinator;

    /// <summary>

    /// DHT manager for distributed peer discovery (null if DHT disabled)

    /// </summary>

    public DhtManager? DhtManager => _dhtCoordinator.DhtManager;

    /// <summary>

    /// Bandwidth coordinator for rate limiting

    /// </summary>

    public GlobalBandwidthCoordinator BandwidthCoordinator => _bandwidthCoordinator;

    /// <summary>

    /// Whether DHT is enabled and running

    /// </summary>

    public bool IsDhtRunning => _dhtCoordinator.IsDhtRunning;

    /// <summary>

    /// Whether DHT is currently initializing (bootstrapping)

    /// </summary>

    public bool IsDhtInitializing => _dhtCoordinator.IsDhtInitializing;

    /// <summary>

    /// Whether DHT is enabled in settings

    /// </summary>

    public bool IsDhtEnabled => _dhtCoordinator.IsDhtEnabled;

    /// <summary>

    /// Number of live DHT nodes in the routing table

    /// </summary>

    public int DhtNodeCount => _dhtCoordinator.DhtNodeCount;

    #endregion

    #region Events

    /// <summary>

    /// Raised when a torrent is added

    /// </summary>

    public event EventHandler<Events.TorrentAddedEventArgs>? TorrentAdded;

    /// <summary>

    /// Raised when a torrent is removed

    /// </summary>

    public event EventHandler<Events.TorrentRemovedEventArgs>? TorrentRemoved;

    /// <summary>

    /// Raised when a torrent's state changes

    /// </summary>

    public event EventHandler<Events.TorrentStatusChangedEventArgs>? TorrentStatusChanged;

    /// <summary>

    /// Raised when a torrent completes downloading

    /// </summary>

    public event EventHandler<Events.TorrentCompletedEventArgs>? TorrentCompleted;

    /// <summary>

    /// Raised when session statistics are updated

    /// </summary>

    public event EventHandler<Events.StatisticsUpdatedEventArgs>? StatisticsUpdated;

    /// <summary>

    /// Raised when DHT state changes (initializing, running, stopped)

    /// </summary>

    public event EventHandler<Events.DhtStateChangedEventArgs>? DhtStateChanged;

    /// <summary>

    /// Raised when a torrent encounters an error

    /// </summary>

    public event EventHandler<Events.TorrentFailedEventArgs>? TorrentFailed;

    /// <summary>

    /// Raised when a torrent reaches its seeding ratio or time limit

    /// </summary>

    public event EventHandler<SeedingLimitReachedEventArgs>? TorrentSeedingLimitReached;

    /// <summary>

    /// Raised when a peer connects (promoted from per-engine events)

    /// </summary>

    public event EventHandler<Events.PeerConnectedEventArgs>? PeerConnected;

    /// <summary>

    /// Raised when a peer disconnects (promoted from per-engine events)

    /// </summary>

    public event EventHandler<Events.PeerDisconnectedEventArgs>? PeerDisconnected;

    /// <summary>

    /// Raised for unified alerts (tracker, disk, peer, torrent)

    /// </summary>

    public event EventHandler<Events.AlertEventArgs>? AlertRaised;

    // Internal event raisers for decomposed managers

    internal void RaiseTorrentAdded(string infoHash, string name)

        => TorrentAdded?.Invoke(this, new Events.TorrentAddedEventArgs(infoHash, name));

    internal void RaiseTorrentRemoved(string infoHash, string name, bool deleteFiles)

        => TorrentRemoved?.Invoke(this, new Events.TorrentRemovedEventArgs(infoHash, name, deleteFiles));

    internal void RaiseTorrentCompleted(string infoHash, string name)

        => TorrentCompleted?.Invoke(this, new Events.TorrentCompletedEventArgs(infoHash, name));

    internal void RaiseTorrentFailed(string infoHash, string name, string error)

        => TorrentFailed?.Invoke(this, new Events.TorrentFailedEventArgs(infoHash, name, error));

    internal void RaiseTorrentStatusChanged(string infoHash, string name, TorrentStatus oldStatus, TorrentStatus newStatus)
        => TorrentStatusChanged?.Invoke(this, new Events.TorrentStatusChangedEventArgs(infoHash, name, oldStatus, newStatus));

    internal void RaiseDhtStateChanged(bool isRunning, bool isInitializing, int nodeCount)

        => DhtStateChanged?.Invoke(this, new Events.DhtStateChangedEventArgs(isRunning, isInitializing, nodeCount));

    internal void RaiseStatisticsUpdated(SessionStatistics snapshot)

        => StatisticsUpdated?.Invoke(this, new Events.StatisticsUpdatedEventArgs(snapshot));

    internal void RaiseSeedingLimitReached(string infoHash, SeedingLimitResult result)

        => TorrentSeedingLimitReached?.Invoke(this, new SeedingLimitReachedEventArgs(

            infoHash, result.TorrentName ?? infoHash, result.Type!.Value, result.Action,

            result.CurrentValue, result.LimitValue));

    #endregion

    #region Constructor

    public TorrentOrchestrator(

        SessionPersistence persistence,

        ILoggerFactory loggerFactory,

        ISecureFileWiper secureFileWiper,

        DeletionWorker deletionWorker,

        ResourceAllocator? resourceAllocator = null,

        AlertManager? alertManager = null,

        UdpSocketManager? udpSocketManager = null,

        IOptionsMonitor<BehaviorSettings>? behaviorMonitor = null,

        IOptionsMonitor<PeerSettings>? peerSettingsMonitor = null,

        IOptionsMonitor<QueueSettings>? queueMonitor = null,

        IOptionsMonitor<TrackerSettings>? trackerMonitor = null,

        IOptionsMonitor<ConnectionSettings>? connectionMonitor = null,

        IOptionsMonitor<DhtSettings>? dhtMonitor = null,

        IOptionsMonitor<EncryptionSettings>? encryptionMonitor = null,

        IOptionsMonitor<WebSeedSettings>? webSeedMonitor = null,

        IOptionsMonitor<ProxySettings>? proxyMonitor = null,

        IOptionsMonitor<VpnSettings>? vpnMonitor = null,

        IOptionsMonitor<AutoSaveSettings>? autoSaveMonitor = null,

        IOptionsMonitor<ProtocolSettings>? protocolMonitor = null,

        IOptionsMonitor<DiskSettings>? diskMonitor = null,

        IOptionsMonitor<I2pSettings>? i2pMonitor = null,

        I2pService? i2pService = null,

        ProfileManager? profileManager = null,

        IOptionsMonitor<ScheduleSettings>? scheduleMonitor = null)

    {

        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));

        var startConcurrency = persistence.Settings.Queue.EngineStartConcurrency;
        _engineStartGate = new SemaphoreSlim(Math.Clamp(startConcurrency, 1, 32));

        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

        _logger = loggerFactory.CreateLogger<TorrentOrchestrator>();

        _secureFileWiper = secureFileWiper ?? throw new ArgumentNullException(nameof(secureFileWiper));

        _deletionWorker = deletionWorker ?? throw new ArgumentNullException(nameof(deletionWorker));

        _torrents = new TorrentCollection();

        _stateIndex = new StateIndex();

        _queue = new QueueManager();

        _statistics = new SessionStatistics { SessionStartTime = DateTime.UtcNow };

        // Create or use provided resource allocators

        _resourceAllocator = resourceAllocator ?? new ResourceAllocator();

        _alertManager = alertManager ?? new AlertManager();

        // Apply settings to resource allocator

        _resourceAllocator.ApplySettings(persistence.Settings);

        // Create bandwidth coordinator

        _bandwidthCoordinator = new GlobalBandwidthCoordinator(

            loggerFactory,

            persistence.Settings.Bandwidth.GlobalDownloadLimit,

            persistence.Settings.Bandwidth.GlobalUploadLimit);

        _bandwidthCoordinator.ApplySettings(persistence.Settings);

        // Shared UDP socket for uTP + DHT

        _udpSocketManager = udpSocketManager;

        // Create UDP tracker packet handler and register on shared socket
        if (_udpSocketManager != null)
        {
            _trackerPacketHandler = new UdpTrackerPacketHandler();
            _udpSocketManager.SetTrackerHandler(_trackerPacketHandler);
        }

        // Resolve proxy connector from settings (STARTUP snapshot; runtime re-creation not yet needed)
        _proxyConnector = ProxyConnectorFactory.Create(persistence.Settings.Proxy);

        // STARTUP: reads before monitors are populated — resolve outgoing interface to bind address
        var bindAddress = InterfaceResolver.Resolve(persistence.Settings.Connection.OutgoingInterface);

        // Create VPN kill-switch if enabled
        var vpnSettings = persistence.Settings.Vpn;
        if (vpnSettings.KillSwitchEnabled)
        {
            var vpnInterface = !string.IsNullOrWhiteSpace(vpnSettings.VpnInterfaceName)
                ? vpnSettings.VpnInterfaceName
                : persistence.Settings.Connection.OutgoingInterface; // STARTUP: before monitors

            if (!string.IsNullOrWhiteSpace(vpnInterface))
            {
                _vpnKillSwitch = new VpnKillSwitch(loggerFactory.CreateLogger<VpnKillSwitch>());
                _vpnKillSwitch.BlockingStateChanged += OnKillSwitchBlockingStateChanged;
            }
        }

        // Create session-level disk space monitor and error recovery manager
        var diskSettings = persistence.Settings.Disk;
        _diskSpaceMonitor = new DiskSpaceMonitor(
            diskSettings.DiskSpaceWarningBytes,
            diskSettings.DiskSpaceCriticalBytes,
            loggerFactory.CreateLogger<DiskSpaceMonitor>());

        _diskErrorRecoveryManager = new DiskErrorRecoveryManager(
            _diskSpaceMonitor,
            diskSettings,
            retryCallback: async (infoHashHex) => await RetryErroredTorrentAsync(infoHashHex),
            loggerFactory.CreateLogger<DiskErrorRecoveryManager>());

        _diskSpaceMonitor.SpaceChanged += (_, evt) =>
        {
            _logger.LogWarning("Disk space {State} on {Drive}: {Free:N0} bytes free",
                evt.State, evt.DriveRoot, evt.FreeBytes);
        };

        // Create peer class manager for IP-based bandwidth classification
        if (persistence.Settings.PeerClasses?.Enabled == true)
        {
            _peerClassManager = new Network.PeerClass.PeerClassManager();
            _peerClassManager.LoadFromSettings(persistence.Settings.PeerClasses);
            _logger.LogDebug("Peer class manager initialized with {Count} classes",
                _peerClassManager.GetAllClasses().Count - 1); // Exclude default class
        }

        // Session-level uTP socket manager (outbound half). Its send callback forwards to the
        // shared UDP socket; the callback only fires on send (after the socket is bound during
        // StartAsync), so constructing it here — before the socket binds — is safe.
        if (_udpSocketManager != null)
        {
            _utpSocketManager = new PeerCommunication.Transport.Utp.UtpSocketManager(
                (data, ep) => _udpSocketManager.SendAsync(data, ep, UdpSendFlags.PeerConnection));
        }

        // Create session-level transport connector with proxy, VPN kill switch, and IP filter
        _transportConnector = new TransportConnector(
            utpManager: _utpSocketManager,
            CreatePeerSettings(),
            holepunchManager: null, // created per-engine
            proxyConnector: _proxyConnector,
            proxyPeerConnections: persistence.Settings.Proxy?.ProxyPeerConnections ?? false,
            killSwitch: _vpnKillSwitch,
            connectionMonitor: connectionMonitor,
            ipFilter: _ipFilter,
            logger: loggerFactory.CreateLogger<TransportConnector>());

        // Initialize shared tracker HTTP client with proxy settings
        var effectiveTrackerMonitor = trackerMonitor ?? new OptionsMonitorShim<TrackerSettings>(CreateTrackerSettings());
        SharedTrackerHttpClient.Initialize(effectiveTrackerMonitor, proxyMonitor);

        // Create engine factory with shared resources

        _engineFactory = new EngineFactory(

            loggerFactory,

            _alertManager,

            _resourceAllocator,

            CreatePeerSettings(),

            trackerMonitor: effectiveTrackerMonitor,

            bandwidthCoordinator: _bandwidthCoordinator,

            database: persistence.Database,

            transportConnector: _transportConnector,
            externalIpVoter: _externalIpVoter,
            behaviorMonitor: behaviorMonitor,
            peerSettingsMonitor: peerSettingsMonitor,
            encryptionMonitor: _encryptionMonitor,
            connectionMonitor: _connectionMonitor,
            webSeedMonitor: _webSeedMonitor,
            ipFilter: _ipFilter,
            peerClassManager: _peerClassManager,
            diskMonitor: _diskMonitor,
            unchokeAllocator: _resourceAllocator.Unchoke,
            udpSocketManager: _udpSocketManager,
            trackerPacketHandler: _trackerPacketHandler,
            proxyMonitor: _proxyMonitor);

        _autoManager = new AutoManager(

            _stateIndex,

            _queue,

            StartTorrentInternal,

            PauseTorrentInternal,

            loggerFactory.CreateLogger<AutoManager>(),

            queueMonitor);

        _queueMonitor = queueMonitor;

        _trackerMonitor = trackerMonitor;

        _connectionMonitor = connectionMonitor;

        _dhtMonitor = dhtMonitor;

        _encryptionMonitor = encryptionMonitor;
        _webSeedMonitor = webSeedMonitor;
        _proxyMonitor = proxyMonitor;
        _vpnMonitor = vpnMonitor;
        _vpnChangeRegistration = vpnMonitor?.OnChange((newSettings, _) => OnVpnSettingsChanged(newSettings));
        _autoSaveMonitor = autoSaveMonitor;
        _protocolMonitor = protocolMonitor;
        _behaviorMonitor = behaviorMonitor;
        _peerMonitor = peerSettingsMonitor;
        _diskMonitor = diskMonitor;
        _i2pMonitor = i2pMonitor;
        _i2pService = i2pService;

        // Create seeding limit enforcer

        _seedingLimitEnforcer = new SeedingLimitEnforcer(

            () => _persistence.Settings,

            loggerFactory.CreateLogger<SeedingLimitEnforcer>());

        // Create decomposed managers

        _lifecycleManager = new TorrentLifecycleManager(this, loggerFactory, _secureFileWiper, _deletionWorker);

        _dhtCoordinator = new DhtCoordinator(this, loggerFactory, udpSocketManager, dhtMonitor, _i2pService);

        _backgroundTaskManager = new BackgroundTaskManager(this, loggerFactory, _seedingLimitEnforcer);

        // Subscribe to BehaviorSettings changes so AutoSequential updates are logged.
        // DownloadCoordinator and PieceSelectionCoordinator read from the monitor lazily
        // on each cycle, so no explicit push is needed — just log the change.
        if (behaviorMonitor != null)
        {
            _behaviorChangeRegistration = behaviorMonitor.OnChange((newSettings, _) =>
            {
                _logger.LogDebug("AutoSequential settings changed: enabled={Enabled}, ratio={Ratio}",
                    newSettings.AutoSequentialInSeederSwarm, newSettings.AutoSequentialRatio);
            });
        }

        // Create profile scheduler for time-based profile/mode switching
        if (profileManager != null)
        {
            _scheduler = new ProfileScheduler(
                persistence.SettingsManager!,
                profileManager,
                loggerFactory.CreateLogger<ProfileScheduler>(),
                getAllTorrents: () => GetAllTorrents().Select(t =>
                    new SchedulerTorrentInfo(t.InfoHash, t.Status.Phase, t.Status.Intent, t.IsAutoManaged, t.UserPaused, t.IsPaused)).ToList(),
                pauseTorrent: hash => PauseTorrentAsync(hash),
                startTorrent: hash => StartTorrentAsync(hash)
            );

            // Boot: start now if the persisted settings already say Enabled — OnChange
            // only fires on transitions, so without this the scheduler would stay idle.
            ProfileScheduler.EnsureMatchesEnabled(_scheduler, persistence.SettingsManager!.Current.Schedule.Enabled);

            // Runtime: same logic on every settings transition.
            _scheduleChangeRegistration = scheduleMonitor?.OnChange((newSettings, _) =>
                ProfileScheduler.EnsureMatchesEnabled(_scheduler, newSettings.Enabled));
        }

    }

    #endregion

    #region Settings Translation

    internal PeerSettings CreatePeerSettings()

    {

        var settings = _persistence.Settings;

        // Use monitor for live connection settings when available (RUNTIME),
        // falls back to persistence (safe for STARTUP before monitors are populated)
        var connSettings = _connectionMonitor?.CurrentValue ?? settings.Connection;

        return new PeerSettings

        {

            MaxConnections = connSettings.MaxConnectionsPerTorrent,

            MaxUploadsPerTorrent = connSettings.MaxUploadsPerTorrent,

            ListenPort = connSettings.ListenPort,

            ConnectTimeout = settings.Peer.ConnectTimeout,

            HandshakeTimeout = settings.Peer.HandshakeTimeout,

            MaxPendingBlocksPerPeer = settings.Peer.MaxPendingBlocksPerPeer,

            EnablePex = (_protocolMonitor?.CurrentValue ?? settings.Protocol).EnablePex,

            InactivityTimeout = settings.Peer.InactivityTimeout,

            PieceTimeout = settings.Peer.PieceTimeout,

            UnchokeInterval = settings.Peer.UnchokeInterval,

            OptimisticUnchokeInterval = settings.Peer.OptimisticUnchokeInterval,

            PrioritizePartialPieces = settings.Behavior.PrioritizePartialPieces,

            StrictEndgameMode = settings.Behavior.StrictEndgameMode,

            CloseRedundantConnections = settings.Behavior.CloseRedundantConnections,

            SeedingOutgoingConnections = settings.Behavior.SeedingOutgoingConnections,

            DiskCacheSize = settings.Disk.CacheSize

        };

    }

    private TrackerSettings CreateTrackerSettings()

    {

        var settings = _persistence.Settings;

        // Use monitor for live connection settings when available (RUNTIME),
        // falls back to persistence (safe for STARTUP before monitors are populated)
        var connSettings = _connectionMonitor?.CurrentValue ?? settings.Connection;

        return new TrackerSettings

        {

            HttpTimeoutSeconds = settings.Tracker.HttpTimeoutSeconds,

            UdpTimeoutSeconds = settings.Tracker.UdpTimeoutSeconds,

            MaxRetries = settings.Tracker.MaxRetries,

            NumWant = settings.Tracker.NumWant,

            ListenPort = connSettings.ListenPort,

            MinAnnounceInterval = settings.Tracker.MinAnnounceInterval,

            UserAgent = (_protocolMonitor?.CurrentValue ?? settings.Protocol).UserAgent,

            ReportRedundantBytes = settings.Behavior.ReportRedundantBytes,

            ReportTrueDownloaded = settings.Behavior.ReportTrueDownloaded

        };

    }

    /// <summary>

    /// Apply updated settings (called when user changes settings).

    /// Updates resource limits, queue settings, auto-save timer, DHT settings,

    /// bandwidth limits, and propagates changes to all running torrent engines at runtime.

    /// </summary>

    public void ApplySettings(GlobalSettings settings)

    {

        // Resource allocator (connection limits, bandwidth limits)

        _resourceAllocator.ApplySettings(settings);

        // Bandwidth coordinator (rate limiting)

        _bandwidthCoordinator.ApplySettings(settings);

        // Queue/Auto-manager settings

        _autoManager.MaxActiveDownloads = settings.Queue.MaxActiveDownloads;

        _autoManager.MaxActiveSeeds = settings.Queue.MaxActiveSeeds;

        _autoManager.MaxActiveTorrents = settings.Queue.MaxActiveTorrents;

        // Slow torrent detection settings

        _autoManager.DontCountSlowTorrents = settings.Queue.DontCountSlowTorrents;

        _autoManager.InactiveDownRate = settings.Queue.InactiveDownRate;

        _autoManager.InactiveUpRate = settings.Queue.InactiveUpRate;

        _autoManager.InactiveGracePeriodSeconds = 60; // hardcoded constant

        // Auto-save timer

        _backgroundTaskManager.UpdateAutoSaveTimer(settings);

        // DHT settings now flow via IOptionsMonitor<DhtSettings> (lazy reads)

        // Apply settings to all running torrent engines

        ApplySettingsToRunningEngines(settings);

    }

    /// <summary>

    /// Apply settings to all running torrent engines.

    /// This enables instant application of settings like upload slots and PEX.

    /// </summary>

    private void ApplySettingsToRunningEngines(GlobalSettings settings)

    {

        foreach (var torrent in _torrents)

        {

            if (torrent.Engine != null)

            {

                try

                {

                    torrent.Engine.ApplySettings(

                        maxUploadsPerTorrent: settings.Connection.MaxUploadsPerTorrent,

                        enablePex: (_protocolMonitor?.CurrentValue ?? settings.Protocol).EnablePex,

                        unchokeIntervalSeconds: settings.Peer.UnchokeInterval,

                        optimisticUnchokeIntervalSeconds: settings.Peer.OptimisticUnchokeInterval,

                        closeRedundantConnections: settings.Behavior.CloseRedundantConnections,

                        autoSequentialInSeederSwarm: settings.Behavior.AutoSequentialInSeederSwarm,

                        prioritizePartialPieces: settings.Behavior.PrioritizePartialPieces,

                        strictEndgameMode: settings.Behavior.StrictEndgameMode,

                        seedingOutgoingConnections: settings.Behavior.SeedingOutgoingConnections);

                }

                catch (Exception ex)

                {

                    _logger.LogWarning(ex, "Failed to apply settings to torrent {InfoHash}", torrent.InfoHash);

                }

            }

        }

    }

    /// <summary>

    /// Apply per-torrent settings to a running engine.

    /// Called when user changes torrent-specific settings like sequential mode.

    /// </summary>

    public void ApplyTorrentSettings(string infoHash, TorrentSettings settings)

    {

        var managed = _torrents.Find(infoHash);

        if (managed == null)

        {

            _logger.LogWarning("Torrent {InfoHash} not found for settings update", infoHash);

            return;

        }

        // Update managed torrent properties

        managed.SequentialDownload = settings.SequentialDownload;

        managed.FirstLastPiecePriority = settings.FirstLastPiecePriority;

        // Apply to running engine if present

        if (managed.Engine != null)

        {

            try

            {

                // Apply sequential mode

                managed.Engine.SetSequentialDownload(settings.SequentialDownload);

                _logger.LogDebug("Applied SequentialDownload={Value} to running engine for {InfoHash}",

                    settings.SequentialDownload, infoHash);

                // Apply first/last piece priority

                managed.Engine.SetFirstLastPiecePriority(settings.FirstLastPiecePriority);

                _logger.LogDebug("Applied FirstLastPiecePriority={Value} to running engine for {InfoHash}",

                    settings.FirstLastPiecePriority, infoHash);

            }

            catch (Exception ex)

            {

                _logger.LogWarning(ex, "Failed to apply settings to running torrent {InfoHash}", infoHash);

            }

        }

        else

        {

            // Engine not running - settings will be applied when started

            _logger.LogDebug("Stored SequentialDownload={Value}, FirstLastPiecePriority={FLP} for {InfoHash} (engine not running)",

                settings.SequentialDownload, settings.FirstLastPiecePriority, infoHash);

        }

    }

    // ── Streaming API (libtorrent-style piece deadlines) ──────────────

    /// <summary>

    /// Set a piece deadline for streaming playback.

    /// </summary>

    public void SetPieceDeadline(string infoHash, int pieceIndex, int deadlineMs,

        bool alertWhenAvailable = false)

    {

        var managed = _torrents.Find(infoHash);

        managed?.Engine?.SetPieceDeadline(pieceIndex, deadlineMs, alertWhenAvailable);

    }

    /// <summary>Remove a piece deadline.</summary>

    public void ResetPieceDeadline(string infoHash, int pieceIndex)

    {

        var managed = _torrents.Find(infoHash);

        managed?.Engine?.ResetPieceDeadline(pieceIndex);

    }

    /// <summary>Clear all piece deadlines for a torrent.</summary>

    public void ClearPieceDeadlines(string infoHash)

    {

        var managed = _torrents.Find(infoHash);

        managed?.Engine?.ClearPieceDeadlines();

    }

    #endregion

    #region Initialization & Shutdown

    /// <summary>

    /// Initialize orchestrator - load torrents from persistence

    /// </summary>

    public async Task InitializeAsync(CancellationToken cancellationToken = default)

    {

        if (_isInitialized)

            return;

        _logger.LogInformation("Initializing torrent orchestrator...");

        _sessionStartTime = DateTime.UtcNow;

        // 1. Ensure persistence is initialized

        if (!_persistence.IsInitialized)

        {

            await _persistence.InitializeAsync(cancellationToken).ConfigureAwait(false);

        }

        // 2. Load session state

        _sessionState = await _persistence.LoadSessionStateAsync().ConfigureAwait(false);

        _logger.LogDebug("Loaded session state");

        // BEP 24: Hydrate ExternalIpVoter from persisted records
        if (_sessionState.ExternalIps.Count > 0)
        {
            _externalIpVoter.HydrateFromRecords(
                _sessionState.ExternalIps.Select(r => new ExternalIpVoteRecord(r.Ip, r.VoteCount, r.LastSeen)));
            _logger.LogDebug("Recovered {Count} external IP votes from session state", _sessionState.ExternalIps.Count);
        }

        // 2b. Load IP filter from file if configured
        var ipFilterFilePath = _persistence.Settings.Connection.IpFilterFilePath;
        if (!string.IsNullOrEmpty(ipFilterFilePath) && System.IO.File.Exists(ipFilterFilePath))
        {
            if (_sessionState?.IpFilter != null)
                vTorrent.Core.Network.IpFilter.IpFilterStartup.LoadFromState(_ipFilter, _sessionState.IpFilter);
            var (loaded, skipped) = await vTorrent.Core.Network.IpFilter.IpFilterLoader.LoadAsync(_ipFilter, ipFilterFilePath, cancellationToken);
            _logger.LogInformation("IP filter loaded: {Loaded} rules, {Skipped} skipped from {Path}", loaded, skipped, ipFilterFilePath);
        }
        else if (_sessionState?.IpFilter != null)
        {
            // No file configured, but session state has IP filter rules (e.g., manually banned IPs)
            vTorrent.Core.Network.IpFilter.IpFilterStartup.LoadFromState(_ipFilter, _sessionState.IpFilter);
        }

        // 3. Apply settings to auto-manager

        ApplySettingsToAutoManager();

        // 3b. Start VPN kill-switch monitoring if configured
        if (_vpnKillSwitch != null)
        {
            var vpnSettings = _persistence.Settings.Vpn;
            var vpnInterface = !string.IsNullOrWhiteSpace(vpnSettings.VpnInterfaceName)
                ? vpnSettings.VpnInterfaceName
                : _persistence.Settings.Connection.OutgoingInterface; // STARTUP: reads before monitors are populated
            _vpnKillSwitch.Start(vpnInterface);
            _logger.LogInformation("VPN kill-switch started monitoring interface '{Interface}'", vpnInterface);
        }

        // 3c. Create and start I2P service if enabled
        if (_i2pMonitor?.CurrentValue.Enabled == true)
        {
            try
            {
                _i2pService = new I2pService(_i2pMonitor, _persistence.DataDirectory, _loggerFactory);
                await _i2pService.StartAsync(cancellationToken).ConfigureAwait(false);
                _engineFactory.SetI2pService(_i2pService);
                _logger.LogInformation("I2P service started (connected: {Connected})", _i2pService.IsConnected);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start I2P service — I2P torrents will not function until SAM bridge is available");
            }
        }

        // Subscribe to I2P settings changes for runtime reload
        // NOTE: OnChange takes Action<T,string?>, not async. Use Task.Run for async work.
        if (_i2pMonitor != null)
        {
            _i2pChangeRegistration = _i2pMonitor.OnChange((newSettings, __) =>
            {
                var _ = Task.Run(async () =>
                {
                    try
                    {
                        if (newSettings.Enabled && (_i2pService == null || !_i2pService.IsConnected))
                        {
                            _i2pService ??= new I2pService(_i2pMonitor!, _persistence.DataDirectory, _loggerFactory);
                            await _i2pService.StartAsync(CancellationToken.None).ConfigureAwait(false);
                            _engineFactory.SetI2pService(_i2pService);
                            _logger.LogInformation("I2P service started via settings change");
                        }
                        else if (!newSettings.Enabled && _i2pService != null)
                        {
                            await _i2pService.StopAsync().ConfigureAwait(false);
                            _logger.LogInformation("I2P service stopped via settings change");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error handling I2P settings change");
                    }
                });
            });
        }

        // 3d. Bind the shared UDP socket before DHT/tracker transports use it.
        // Ordering is load-bearing: DHT (UdpDhtTransport) and UDP trackers read
        // this started socket; binding after DHT start would route them direct.
        if (_udpSocketManager != null)
        {
            try
            {
                var conn = _connectionMonitor?.CurrentValue ?? _persistence.Settings.Connection;
                var vpn = _vpnMonitor?.CurrentValue ?? _persistence.Settings.Vpn;
                var udpInterface = !string.IsNullOrWhiteSpace(vpn.VpnInterfaceName)
                    ? vpn.VpnInterfaceName
                    : conn.OutgoingInterface;
                var bindAddress = InterfaceResolver.Resolve(udpInterface) ?? System.Net.IPAddress.Any;
                var proxySettings = _proxyMonitor?.CurrentValue ?? _persistence.Settings.Proxy;
                await _udpSocketManager.StartAsync(
                    new System.Net.IPEndPoint(bindAddress, conn.ListenPort),
                    cancellationToken,
                    proxySettings).ConfigureAwait(false);
                _logger.LogInformation(
                    "Shared UDP socket bound on {Address}:{Port} (proxy={ProxyType})",
                    bindAddress, conn.ListenPort, proxySettings.Type);

                if (_utpSocketManager != null)
                {
                    _udpSocketManager.SetUtpHandler(_utpSocketManager);
                    _logger.LogInformation("uTP transport commissioned (session-level)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to bind shared UDP socket — DHT and UDP-tracker traffic will be " +
                    "degraded until restart. Session startup continues.");
            }
        }

        // 4. Initialize DHT in background (non-blocking) if enabled

        _dhtCoordinator.StartDhtInBackground();

        // --- Session-level inbound peer listener (TCP + uTP). ---
        var inboundPeerSettings = CreatePeerSettings();
        var inboundListenPort = _persistence.Settings.Connection.ListenPort;
        var inboundListener = new TransportListener(
            utpManager: _utpSocketManager,
            inboundPeerSettings,
            _loggerFactory.CreateLogger<TransportListener>(),
            connectionMonitor: _connectionMonitor);
        _incomingDispatcher = new IncomingConnectionDispatcher(
            inboundListener,
            resolvePeerManager: hex =>
            {
                var t = _torrents.Find(hex);
                return t?.Engine?.PeerManagerInternal as PeerManager;
            },
            req2HashLookup: req2 =>
            {
                var hexHash = Convert.ToHexString(req2);
                var t = _torrents.FindByReq2Hash(hexHash);
                return t != null ? Convert.FromHexString(t.InfoHash) : null;
            },
            encryptionMonitor: _encryptionMonitor ?? new OptionsMonitorShim<EncryptionSettings>(new EncryptionSettings()),
            loggerFactory: _loggerFactory,
            connectedPeerCount: () => _torrents.Sum(t => (t.Engine?.PeerManagerInternal as PeerManager)?.ConnectedPeerCount ?? 0),
            maxSessionConnections: () => inboundPeerSettings.MaxConnections * Math.Max(1, _torrents.Count));
        try
        {
            await _incomingDispatcher.StartAsync(
                new System.Net.IPEndPoint(System.Net.IPAddress.Any, inboundListenPort)).ConfigureAwait(false);
            _logger.LogInformation("Inbound peer listener started on TCP port {Port}", _incomingDispatcher.BoundPort);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to start inbound peer listener on port {Port}; inbound peers disabled this session",
                inboundListenPort);
        }

        // 5. Load all torrents from database

        var records = await _persistence.LoadAllTorrentsAsync().ConfigureAwait(false);

        _logger.LogInformation("Found {Count} torrents in database", records.Count);

        // 6. Batch-load categories and tags upfront (2 queries instead of 2N)
        var allCategories = (await _persistence.GetAllCategoriesAsync().ConfigureAwait(false))
            .ToDictionary(c => c.Id);
        var allTagsMapping = await _persistence.GetAllTorrentTagsMappingAsync().ConfigureAwait(false);

        // 7. Restore torrents in parallel (bounded concurrency to avoid disk thrashing)
        var restoreStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _embeddedRestoreCount = 0;
        _diskFallbackRestoreCount = 0;
        var restoreConcurrency = Math.Clamp(_persistence.Settings.Queue.EngineStartConcurrency, 1, 32);
        await Parallel.ForEachAsync(
            records,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = restoreConcurrency,
                CancellationToken = cancellationToken
            },
            async (record, ct) =>
            {
                try
                {
                    await RestoreTorrentAsync(record, allCategories, allTagsMapping, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to restore torrent {Name}", record.Name);
                }
            });

        restoreStopwatch.Stop();
        _logger.LogInformation("Restored {Count} torrents in {ElapsedMs}ms (embedded: {Embedded}, disk fallback: {Disk})",
            records.Count, restoreStopwatch.ElapsedMilliseconds, _embeddedRestoreCount, _diskFallbackRestoreCount);

        // 7. Start background services

        _backgroundTaskManager.Start();

        // 8. Start auto-manager

        _autoManager.Start();

        // 9. Start profile scheduler if enabled
        if (_scheduler != null && _persistence.Settings.Schedule.Enabled)
            _scheduler.Start();

        _isInitialized = true;

        _logger.LogInformation("Orchestrator initialized with {Count} torrents", _torrents.Count);

    }

    /// <summary>

    /// Graceful shutdown

    /// </summary>

    public async ValueTask DisposeAsync()

    {

        if (_isShuttingDown)

            return;

        _isShuttingDown = true;

        _logger.LogInformation("Shutting down torrent orchestrator...");

        try

        {

            // 0. Unregister settings change listeners and stop VPN kill-switch
            _behaviorChangeRegistration?.Dispose();
            _scheduleChangeRegistration?.Dispose();
            _vpnChangeRegistration?.Dispose();
            _i2pChangeRegistration?.Dispose();
            _vpnKillSwitch?.Dispose();

            // 0b. Stop I2P service
            if (_i2pService != null)
            {
                try { await _i2pService.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error stopping I2P service"); }
            }

            // 0c. Stop inbound peer listener
            if (_incomingDispatcher != null)
            {
                try { await _incomingDispatcher.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error stopping inbound peer listener"); }
            }

            // 1. Stop profile scheduler (if running)
            if (_scheduler != null)
            {
                try { await _scheduler.StopAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error stopping profile scheduler"); }
            }

            // 2. Stop auto-manager and background services (sync, fast)

            _autoManager.Stop();

            _autoManager.Dispose();

            _backgroundTaskManager.Stop();

            // 2. Capture torrent states BEFORE stopping engines

            var shutdownData = _torrents.Select(t =>

            {

                var status = t.GetStatus();

                return new TorrentShutdownUpdate(

                    t.InfoHash,

                    t.Progress,

                    t.Statistics.AllTimeUploaded,

                    t.Statistics.AllTimeDownloaded,

                    t.Statistics.AllTimePayloadUploaded,

                    t.Statistics.AllTimePayloadDownloaded,

                    (long)t.Statistics.ActiveDuration.TotalSeconds,

                    (long)t.Statistics.SeedingDuration.TotalSeconds,

                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),

                    t.Statistics.LastUpload.HasValue

                        ? new DateTimeOffset(t.Statistics.LastUpload.Value).ToUnixTimeSeconds()

                        : null,

                    t.Statistics.LastDownload.HasValue

                        ? new DateTimeOffset(t.Statistics.LastDownload.Value).ToUnixTimeSeconds()

                        : null,

                    t.IsFinished,

                    t.IsSeed,

                    // Orthogonal state dimensions

                    TransferPhase: status.Phase.ToString(),

                    FileOperation: status.FileOp.ToString(),

                    UserIntent: status.Intent.ToString(),

                    Health: status.Error.HasValue ? "Error" : (status.MissingFiles ? "MissingFiles" : "Ok"),

                    ErrorMessage: status.Error?.Message

                );

            }).ToList();

            // === PHASE A: Save resume data (parallel per-torrent) ===

            // Must complete before engines stop — reads bitfield from running engine

            var resumeTasks = _torrents.Select(async torrent =>
            {
                try
                {
                    UpdateResumeDataFromTorrent(torrent);

                    // Mark clean shutdown: set NoVerifyFiles so TryFastResumeAsync takes the
                    // instant path and skips CheckFilesModifiedAsync (O(N files) stat calls).
                    // This is ONLY set here in DisposeAsync — NOT in UpdateResumeDataFromTorrent
                    // which is also called from periodic saves and lifecycle transitions.
                    if (torrent.ResumeData != null)
                        torrent.ResumeData.Flags |= TorrentFlags.NoVerifyFiles;

                    await _persistence.SaveResumeDataAsync(torrent.InfoHash, torrent.ResumeData).ConfigureAwait(false);

                    _logger.LogDebug("Saved resume data for {Name}, have pieces: {Count}",
                        torrent.Name, torrent.ResumeData?.GetCompletedPieceCount() ?? 0);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save resume data for {Name}", torrent.Name);
                }
            });

            await Task.WhenAll(resumeTasks).ConfigureAwait(false);

            // === PHASE B: Stop DHT + all engines (parallel) ===

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            await Task.WhenAll(

                _dhtCoordinator.StopDhtAsync(),

                StopAllEnginesAsync(cts.Token)

            ).ConfigureAwait(false);

            // === PHASE C: All DB writes (parallel) ===

            var dbTasks = new List<Task>();

            dbTasks.Add(_persistence.BatchUpdateTorrentsAsync(shutdownData));

            if (_sessionState != null)

            {

                UpdateSessionState();

                dbTasks.Add(_persistence.SaveSessionStateAsync(_sessionState));

            }

            var queueUpdates = _queue.GetQueuePositionUpdates();

            dbTasks.Add(_persistence.BatchUpdateQueuePositionsAsync(queueUpdates));

            await Task.WhenAll(dbTasks).ConfigureAwait(false);

            // Drain and stop the deletion worker (completes pending I/O jobs)
            await _deletionWorker.DisposeAsync().ConfigureAwait(false);

            // Dispose session-level uTP socket manager (outbound half) before the shared UDP
            // socket is stopped/disposed (owned outside the orchestrator, via DI).
            _utpSocketManager?.Dispose();

            // Dispose bandwidth coordinator

            _bandwidthCoordinator.Dispose();

            // Dispose disk I/O subsystem
            _diskErrorRecoveryManager.Dispose();
            _diskSpaceMonitor.Dispose();
            _engineStartGate.Dispose();

            _logger.LogInformation("Orchestrator shutdown complete");

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Error during orchestrator shutdown");

        }

    }

    private void ApplySettingsToAutoManager()

    {

        var settings = _persistence.Settings;

        _autoManager.MaxActiveDownloads = settings.Queue.MaxActiveDownloads;

        _autoManager.MaxActiveSeeds = settings.Queue.MaxActiveSeeds;

        _autoManager.MaxActiveTorrents = settings.Queue.MaxActiveTorrents;

    }

    #endregion

    #region DHT Management (delegated to DhtCoordinator)

    public Task EnableDhtAsync() => _dhtCoordinator.EnableDhtAsync();

    public Task DisableDhtAsync() => _dhtCoordinator.DisableDhtAsync();

    public Task ToggleDhtAsync() => _dhtCoordinator.ToggleDhtAsync();

    #endregion

    #region Torrent Management (delegated to TorrentLifecycleManager)

    public Task<TorrentHandle> AddTorrentAsync(

        string torrentFilePath,

        string? savePath = null,

        bool startImmediately = true,

        CancellationToken cancellationToken = default)

        => _lifecycleManager.AddTorrentAsync(torrentFilePath, savePath, startImmediately, cancellationToken);

    public Task<TorrentHandle> AddTorrentFromBytesAsync(

        byte[] torrentBytes,

        string? savePath = null,

        bool startImmediately = true,

        string? torrentFilePath = null,

        CancellationToken cancellationToken = default)

        => _lifecycleManager.AddTorrentFromBytesAsync(torrentBytes, savePath, startImmediately, torrentFilePath, cancellationToken);

    public Task<TorrentHandle> AddMagnetLinkAsync(

        string magnetUri,

        string? savePath = null,

        bool startImmediately = true,

        CancellationToken cancellationToken = default)

        => _lifecycleManager.AddMagnetLinkAsync(magnetUri, savePath, startImmediately, cancellationToken);

    public Task<DeleteTorrentFilesResult?> RemoveTorrentAsync(

        string infoHash, bool deleteFiles = false,

        bool secureWipe = false, bool wipeMetadata = false,

        IProgress<DeletionProgress>? progress = null,

        CancellationToken cancellationToken = default)

        => _lifecycleManager.RemoveTorrentAsync(infoHash, deleteFiles, secureWipe, wipeMetadata, progress, cancellationToken);

    public Task DeleteRemainingFilesAsync(string torrentDirectory, string savePath)

        => _lifecycleManager.DeleteRemainingFilesAsync(torrentDirectory, savePath);

    public Task StartTorrentAsync(string infoHash, CancellationToken cancellationToken = default)

        => _lifecycleManager.StartTorrentAsync(infoHash, cancellationToken);

    public Task PauseTorrentAsync(string infoHash, CancellationToken cancellationToken = default)

        => _lifecycleManager.PauseTorrentAsync(infoHash, cancellationToken);

    public Task ForceRecheckAsync(string infoHash, CancellationToken cancellationToken = default, bool resume = false)

        => _lifecycleManager.ForceRecheckAsync(infoHash, resume, cancellationToken);

    public Task ForceStartAsync(string infoHash, CancellationToken cancellationToken = default)

        => _lifecycleManager.ForceStartAsync(infoHash, cancellationToken);

    public Task<bool> ChangeSavePathAsync(string infoHash, string newSavePath, CancellationToken cancellationToken = default)

        => _lifecycleManager.ChangeSavePathAsync(infoHash, newSavePath, cancellationToken);

    public async Task ToggleSuperSeedingAsync(string infoHash)
    {
        var managed = _torrents.Find(infoHash);
        if (managed?.Engine == null) return;

        var currentlyEnabled = managed.Engine.SuperSeedManagerInternal?.IsEnabled ?? false;
        await managed.Engine.SetSuperSeedingAsync(!currentlyEnabled).ConfigureAwait(false);
    }

    #endregion

    #region Queries

    /// <summary>

    /// Get torrent by info hash

    /// </summary>

    public TorrentHandle? GetTorrent(string infoHash)

    {

        var managed = _torrents.Find(infoHash);

        return managed != null ? new TorrentHandle(managed) : null;

    }

    /// <summary>
    /// Creates a read-only ManagedTorrentView DTO for the given info hash.
    /// Returns null if the torrent is not found.
    /// </summary>
    public ManagedTorrentView? GetTorrentView(string infoHash)
    {
        if (string.IsNullOrEmpty(infoHash)) return null;
        return _torrents.Find(infoHash)?.ToView();
    }

    /// <summary>

    /// Get the ManagedTorrent instance by info hash.

    /// Returns null if not found.

    /// </summary>

    internal ManagedTorrent? GetManagedTorrent(string infoHash)

    {

        if (string.IsNullOrEmpty(infoHash)) return null;

        return _torrents.Find(infoHash);

    }

    /// <summary>

    /// Get all torrents

    /// </summary>

    public IReadOnlyList<TorrentHandle> GetAllTorrents()

    {

        return _torrents.ToList().Select(t => new TorrentHandle(t)).ToList();

    }

    /// <summary>

    /// Get torrents by state

    /// </summary>

    public IReadOnlyList<TorrentHandle> GetTorrentsByPhase(TransferPhase phase)
    {
        var torrents = phase switch
        {
            TransferPhase.Downloading => _stateIndex.Downloading,
            TransferPhase.Seeding => _stateIndex.Seeding,
            TransferPhase.Connecting => _stateIndex.Connecting,
            TransferPhase.Allocating or TransferPhase.CheckingFiles or TransferPhase.CheckingResumeData => (IReadOnlyCollection<ManagedTorrent>)_stateIndex.Checking,
            TransferPhase.Idle => _stateIndex.Stopped,
            _ => Array.Empty<ManagedTorrent>()
        };
        return torrents.Select(t => new TorrentHandle(t)).ToList();
    }

    /// <summary>

    /// Get download queue in order

    /// </summary>

    public IReadOnlyList<TorrentHandle> GetDownloadQueue()

    {

        return _queue.GetDownloadQueue().Select(t => new TorrentHandle(t)).ToList();

    }

    /// <summary>

    /// Move a torrent to the top of its queue (highest priority).

    /// Following libtorrent's queue_position_top behavior.

    /// </summary>

    public void SetQueuePositionTop(string infoHash)

    {

        var managed = _torrents.Find(infoHash);

        if (managed == null)

        {

            _logger.LogWarning("Torrent {InfoHash} not found for queue position change", infoHash);

            return;

        }

        _queue.QueueTop(managed);

        _logger.LogDebug("Moved torrent {Name} to top of queue", managed.Name);

    }

    /// <summary>

    /// Move a torrent to the bottom of its queue (lowest priority).

    /// Following libtorrent's queue_position_bottom behavior.

    /// </summary>

    public void SetQueuePositionBottom(string infoHash)

    {

        var managed = _torrents.Find(infoHash);

        if (managed == null)

        {

            _logger.LogWarning("Torrent {InfoHash} not found for queue position change", infoHash);

            return;

        }

        _queue.QueueBottom(managed);

        _logger.LogDebug("Moved torrent {Name} to bottom of queue", managed.Name);

    }

    /// <summary>

    /// Move a torrent up one position in its queue.

    /// Following libtorrent's queue_position_up behavior.

    /// </summary>

    public void SetQueuePositionUp(string infoHash)

    {

        var managed = _torrents.Find(infoHash);

        if (managed == null)

        {

            _logger.LogWarning("Torrent {InfoHash} not found for queue position change", infoHash);

            return;

        }

        _queue.QueueUp(managed);

        _logger.LogDebug("Moved torrent {Name} up in queue", managed.Name);

    }

    /// <summary>

    /// Move a torrent down one position in its queue.

    /// Following libtorrent's queue_position_down behavior.

    /// </summary>

    public void SetQueuePositionDown(string infoHash)

    {

        var managed = _torrents.Find(infoHash);

        if (managed == null)

        {

            _logger.LogWarning("Torrent {InfoHash} not found for queue position change", infoHash);

            return;

        }

        _queue.QueueDown(managed);

        _logger.LogDebug("Moved torrent {Name} down in queue", managed.Name);

    }

    #endregion

    #region Bulk Operations

    public Task PauseAllAsync(CancellationToken cancellationToken = default)

        => _lifecycleManager.PauseAllAsync(cancellationToken);

    public Task ResumeAllAsync(CancellationToken cancellationToken = default)

        => _lifecycleManager.ResumeAllAsync(cancellationToken);

    #endregion

    #region Internal Engine Management

    /// <summary>

    /// Internal method to start a torrent (called by auto-manager)

    /// </summary>

    internal bool StartTorrentInternal(ManagedTorrent managed)

    {

        if (managed.Engine != null)

        {

            // Orthogonal state model: paused engines stay alive. A start/queue-grant
            // on such a torrent must resume it — returning false here left
            // auto-managed torrents permanently Queued (AutoManager.StartCandidates
            // retried the same dead end every recalculation).
            if (managed.Engine.IsPaused)
            {
                _logger.LogInformation("Torrent {Name} has a paused engine — resuming instead of starting", managed.Name);
                var pausedEngine = managed.Engine;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await pausedEngine.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Mirror the fresh-start error path: surface the failure to
                        // alerts and the UI, not just the engine state controller.
                        _logger.LogError(ex, "Engine resume failed for {Name}", managed.Name);
                        // Deliberate trade-off: ResumeAsync's own catch already posted
                        // TorrentError { ErrorCode = "EngineError" } to the state controller,
                        // and this SetError overwrites it without an ErrorCode. But SetError
                        // also records Statistics.Error (and has no error-code overload), so
                        // we keep it to preserve that bookkeeping.
                        managed.SetError(ex.Message);
                        _alertManager.Post(new TorrentErrorAlert(managed.InfoHash, ex.Message));
                        TorrentFailed?.Invoke(this, new Events.TorrentFailedEventArgs(managed.InfoHash, managed.Name, ex.Message));
                    }
                });

                // ResumeAsync posts IntentTrigger.Activate (legal from both Paused and
                // Queued). Mirror the Queued→Active flip done for the fresh-start path
                // below so the orchestrator-side status converges immediately.
                var pausedStatus = managed.GetStatus();
                if (pausedStatus.Intent == UserIntent.Queued)
                {
                    UpdateStatus(managed, pausedStatus with { Intent = UserIntent.Active });
                }
                return true;
            }

            _logger.LogDebug("Torrent {Name} already has an engine", managed.Name);

            return false;

        }

        if (managed.Torrent == null)

        {

            _logger.LogWarning("Cannot start torrent without metadata: {InfoHash}", managed.InfoHash);

            return false;

        }

        // VPN kill switch: do not start torrents while VPN is blocked
        if (managed.IsVpnBlocked)
        {
            _logger.LogDebug("Torrent {Name} is VPN-blocked, not starting", managed.Name);
            return false;
        }

        try

        {

            _logger.LogDebug("Starting engine for: {Name}", managed.Name);

            // Check resource availability

            if (!_resourceAllocator.CanOpenConnection())

            {

                _logger.LogDebug("No connection slots available, keeping {Name} in queue", managed.Name);

                return false;

            }

            // Check I2P concurrency limit before starting an I2P torrent
            if (managed.IsI2p)
            {
                var maxI2p = _i2pMonitor?.CurrentValue.MaxActiveI2pTorrents ?? 3;
                var activeI2pCount = _torrents.Count(t => t.IsI2p && t.IsEngineRunning);
                if (activeI2pCount >= maxI2p)
                {
                    _logger.LogDebug("I2P torrent limit reached ({Active}/{Max}), keeping {Name} in queue",
                        activeI2pCount, maxI2p, managed.Name);
                    return false;
                }
            }

            // Create engine via factory (wires events to AlertManager)

            var engineSettings = EngineSettings.FromManagedTorrent(managed);

            managed.Engine = _engineFactory.Create(managed, engineSettings);

            // BEP 52: Pass merkle trees to engine for v2/hybrid torrents

            managed.Engine.SetMerkleTrees(managed.MerkleTrees);

            // Wire orchestrator-level events

            WireEngineEvents(managed);

            // Apply resume data if available
            _logger.LogWarning("[DIAG] StartTorrentInternal: HavePieces null={IsNull}, HavePieces.Length={Len}, " +
                "Flags={Flags}, IsFinished={IsFinished}, ResumeData.PieceCount={PieceCount}",
                managed.ResumeData.HavePieces == null,
                managed.ResumeData.HavePieces?.Length ?? -1,
                managed.ResumeData.Flags,
                managed.IsFinished,
                managed.ResumeData.PieceCount);

            if (managed.ResumeData.HavePieces != null)

            {

                managed.Engine.SetResumeDataProvider(

                    new ManagedTorrentResumeProvider(managed));

            }

            // Transfer pending file priorities to the engine so they're applied

            // during Phase 5 initialization, before the download loop starts.

            // If PendingFilePriorities was consumed by a previous engine run,

            // restore from ResumeData.FilePriorities (persisted to DB).

            if (managed.PendingFilePriorities == null

                && managed.ResumeData.FilePriorities != null

                && managed.ResumeData.FilePriorities.Count > 0)

            {

                var fileCount = managed.Torrent!.Info.Files?.Count ?? 1;

                var restored = new FilePriority[fileCount];

                for (int i = 0; i < fileCount; i++)

                    restored[i] = FilePriority.Normal;

                foreach (var (index, priority) in managed.ResumeData.FilePriorities)

                {

                    if (index >= 0 && index < fileCount)

                        restored[index] = (FilePriority)priority;

                }

                managed.PendingFilePriorities = restored;

            }

            if (managed.PendingFilePriorities != null)

            {

                managed.Engine.SetPendingFilePriorities(managed.PendingFilePriorities);

                managed.PendingFilePriorities = null;

            }

            // WebSeedSettings: engine reads from IOptionsMonitor<WebSeedSettings> lazily (Task 8)

            // Pass disk settings to engine (read from global settings)

            managed.Engine.SetDiskSettings(Settings.Disk);

            // EncryptionSettings: PeerManager now reads from IOptionsMonitor<EncryptionSettings> directly

            // Start the engine asynchronously

            _ = StartEngineAsync(managed);

            // Transition from Queued → Active now that the engine is running
            var currentStatus = managed.GetStatus();
            if (currentStatus.Intent == UserIntent.Queued)
            {
                var activeStatus = currentStatus with { Intent = UserIntent.Active };
                UpdateStatus(managed, activeStatus);
            }

            // ConnectSeedEveryNDownload: count download connection attempts and inject a
            // seed torrent start after every N downloads to keep seeds active.
            if (!managed.IsFinished)
            {
                _downloadConnectAttempts++;
                var n = _queueMonitor?.CurrentValue.ConnectSeedEveryNDownload ?? 10;
                if (_downloadConnectAttempts >= n)
                {
                    _downloadConnectAttempts = 0;
                    // Try to start a queued seed torrent
                    var seedCandidates = _queue.GetQueuedSeedCandidates();
                    if (seedCandidates.Count > 0)
                    {
                        var seedCandidate = seedCandidates[0];
                        _logger.LogDebug(
                            "Seed connect injection after {N} download connections: starting seed {Name}",
                            n, seedCandidate.Name);
                        StartTorrentInternal(seedCandidate);
                    }
                    else
                    {
                        // TODO: select and connect to a known seed from peer candidates
                        _logger.LogDebug("Seed connect injection after {N} download connections (no queued seeds available)", n);
                    }
                }
            }

            return true;

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Failed to start engine for {Name}", managed.Name);

            managed.SetError(ex.Message);

            _alertManager.Post(new TorrentErrorAlert(managed.InfoHash, ex.Message));

            TorrentFailed?.Invoke(this, new Events.TorrentFailedEventArgs(managed.InfoHash, managed.Name, ex.Message));

            return false;

        }

    }

    private async Task StartEngineAsync(ManagedTorrent managed)

    {

        // Stagger engine starts — at most 3 concurrent to avoid disk/CPU thrashing
        await _engineStartGate.WaitAsync().ConfigureAwait(false);

        try

        {

            await managed.Engine!.StartAsync().ConfigureAwait(false);

            try

            {

            // Load runtime-added web seeds from database
            if (managed.Engine.WebSeedManagerInternal != null)
            {
                try
                {
                    var dbSeeds = await _persistence.GetWebSeedsAsync(managed.InfoHash).ConfigureAwait(false);
                    foreach (var dbSeed in dbSeeds)
                    {
                        var type = dbSeed.Type == "BEP17"
                            ? Download.WebSeedType.BEP17
                            : Download.WebSeedType.BEP19;
                        managed.Engine.WebSeedManagerInternal.AddSeed(dbSeed.Url, type);
                    }
                    if (dbSeeds.Count > 0)
                        _logger.LogDebug("Loaded {Count} runtime-added web seeds from database for {InfoHash}",
                            dbSeeds.Count, managed.InfoHash);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load runtime web seeds from database for {InfoHash}", managed.InfoHash);
                }
            }

            // CRITICAL FIX: Trust the engine's status decision (based on actual bitfield)
            // The engine decides seeding vs downloading in StartOperationsAsync() based on _localBitfield.IsComplete
            // We should NOT override this with database flags - the bitfield is the source of truth

            var currentStatus = managed.GetStatus();

            // Sync the IsFinished flag based on actual engine status
            // This ensures database flags stay in sync with reality
            if (currentStatus.Phase == TransferPhase.Seeding)
            {
                if (!managed.IsFinished)
                {
                    _logger.LogDebug("Engine detected complete torrent, syncing IsFinished=true: {Name}", managed.Name);
                    managed.IsFinished = true;
                }
            }

            managed.ResumeData.IsPaused = false;

            // Clear one-shot NoVerifyFiles flag — recheck trust consumed by fast resume.
            // Subsequent restarts will use normal verification.
            managed.ResumeData.Flags &= ~TorrentFlags.NoVerifyFiles;

            managed.LastActiveTime = DateTime.UtcNow;

            // Register with DHT for peer discovery

            _dhtCoordinator.RegisterTorrentWithDht(managed);

            // Flush any DHT peers that were discovered before engine started

            // (e.g., during magnet link metadata download)

            await _dhtCoordinator.FlushPendingDhtPeersAsync(managed).ConfigureAwait(false);

            _alertManager.Post(new TorrentResumedAlert(managed.InfoHash));

            // Validate state consistency for debugging (following libtorrent pattern)

            ValidateStateConsistency(managed);

            }

            catch (Exception ex)

            {

                // The engine is already running at this point. Treat failures in
                // post-start bookkeeping as degraded startup, not a torrent failure,
                // otherwise the UI can briefly show Error before the next engine
                // phase update clears it.
                _logger.LogWarning(ex,
                    "Post-start initialization failed for {Name} after engine startup; leaving torrent running",
                    managed.Name);
            }

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Engine start failed for {Name}", managed.Name);

            managed.SetError(ex.Message);

            var errorStatus = managed.GetStatus() with { Error = new TorrentError { Message = ex.Message } };
            UpdateStatus(managed, errorStatus);

            _alertManager.Post(new TorrentErrorAlert(managed.InfoHash, ex.Message));

            TorrentFailed?.Invoke(this, new Events.TorrentFailedEventArgs(managed.InfoHash, managed.Name, ex.Message));

        }
        finally
        {
            _engineStartGate.Release();
        }

    }

    /// <summary>

    /// Validates state consistency between engine, managed torrent, and database flags.

    /// Logs warnings for inconsistencies that could indicate bugs.

    /// Following libtorrent's principle: the bitfield is the source of truth.

    /// </summary>

    private void ValidateStateConsistency(ManagedTorrent managed)
    {
        var engine = managed.Engine;
        if (engine == null) return;

        var bitfield = engine.GetPieceBitfield();
        var bitfieldComplete = bitfield != null && !bitfield.Cast<bool>().Contains(false);

        var status = managed.GetStatus();

        // Check 1: Bitfield says complete but not seeding phase
        if (bitfieldComplete && status.Phase != TransferPhase.Seeding)
        {
            _logger.LogWarning(
                "State inconsistency: Bitfield complete but phase is {Phase} for {Name}",
                status.Phase, managed.Name);
        }

        // Check 2: IsFinished flag disagrees with bitfield
        if (managed.IsFinished && !bitfieldComplete)
        {
            _logger.LogWarning(
                "State inconsistency: IsFinished=true but bitfield incomplete for {Name}. Correcting flag.",
                managed.Name);
            managed.IsFinished = false;  // Correct the flag to match reality
        }

        // Check 3: Seeding phase but IsFinished not set
        if (status.Phase == TransferPhase.Seeding && !managed.IsFinished)
        {
            _logger.LogWarning(
                "State inconsistency: Phase is Seeding but IsFinished=false for {Name}. Correcting flag.",
                managed.Name);
            managed.IsFinished = true;  // Sync flag with status
        }
    }

    private void WireEngineEvents(ManagedTorrent managed)

    {

        var engine = managed.Engine!;

        var infoHash = managed.InfoHash;

        // Task 8: Subscribe to controller's StatusChanged (replaces old engine.StatusUpdated).
        managed.StateController.StatusChanged += (_, args) =>
        {
            // Update multi-dimensional state index
            _stateIndex.UpdateStatus(managed, args.OldStatus, args.NewStatus);

            // Mirror to statistics
            managed.Statistics.Phase = args.NewStatus.Phase;
            managed.Statistics.Intent = args.NewStatus.Intent;
            managed.Statistics.Error = args.NewStatus.Error;

            // Notify external listeners
            TorrentStatusChanged?.Invoke(this, new Events.TorrentStatusChangedEventArgs(
                managed.InfoHash, managed.Name, args.OldStatus, args.NewStatus));

            // Persist intent to database
            _ = PersistStateChangeAsync(managed, args.NewStatus.Intent.ToString());
        };

        // Task 8: ChannelDrained fires when the controller's command channel is empty.
        // Use it to trigger auto-manager recalculation after a batch of state changes.
        managed.StateController.ChannelDrained += () =>
        {
            _autoManager?.Trigger();
        };

        // Progress updates - update statistics

        engine.ProgressChanged += (s, e) =>

        {

            ApplyProgressEventStatistics(managed.Statistics, e);

            // Sync session transfer stats for average speed calculation

            managed.Statistics.SessionDownloaded = engine.TotalDownloaded;

            managed.Statistics.SessionUploaded = engine.TotalUploaded;

            // Sync verified download stats (for accurate session progress tracking)

            managed.Statistics.SessionVerifiedDownloaded = engine.VerifiedDownloaded;

            // Sync payload stats (actual file data, excludes protocol overhead)

            managed.Statistics.SessionPayloadDownloaded = engine.PayloadDownloaded;

            managed.Statistics.SessionPayloadUploaded = engine.PayloadUploaded;

            managed.Statistics.PayloadDownloadRate = (int)engine.PayloadDownloadRate;

            managed.Statistics.PayloadUploadRate = (int)engine.PayloadUploadRate;

        };

        // Download completed - transition to seeding

        engine.DownloadCompleted += (s, e) =>

        {

            // Guard: don't fire completion events on re-completion after recheck.
            // libtorrent uses m_complete_sent flag for this same purpose.
            // Check both IsFinished (runtime flag) and CompletedTime (persisted in DB)
            // to handle race conditions where IsFinished may be transiently reset
            // during recheck → engine restart sequences.
            var wasAlreadyFinished = managed.IsFinished || managed.CompletedTime.HasValue;

            _logger.LogWarning("[DIAG] DownloadCompleted handler: wasAlreadyFinished={WasFinished}, IsFinished={IsFinished}, CompletedTime={CompletedTime}, name={Name}",
                wasAlreadyFinished, managed.IsFinished, managed.CompletedTime, managed.Name);

            managed.IsFinished = true;

            _queue.MoveToSeedQueue(managed);

            if (!wasAlreadyFinished)
            {
                _logger.LogWarning("[DIAG] DownloadCompleted: FIRING TorrentCompleted notification for {Name}", managed.Name);
                // First completion — fire notification, set completed time
                managed.CompletedTime = DateTime.UtcNow;
                managed.Statistics.CompletedTime = managed.CompletedTime;

                TorrentCompleted?.Invoke(this, new Events.TorrentCompletedEventArgs(infoHash, managed.Name));

                // Mark as completed in database and save resume data
                _ = OnTorrentCompletedAsync(managed);
            }
            else
            {
                _logger.LogWarning("[DIAG] DownloadCompleted: notification SUPPRESSED (wasAlreadyFinished) for {Name}", managed.Name);
            }

            // Download slot freed — wake auto-manager so it can start a queued torrent.

            _autoManager?.Trigger();

        };

    }

    /// <summary>
    /// Applies statistics fields sourced from the engine's ProgressChanged event args.
    /// TotalDone/TotalWantedDone are deliberately NOT written here: e.BytesVerified is
    /// session-scoped (resets every engine start, excludes fast-resumed pieces), while
    /// TotalDone/TotalWantedDone are possession state owned by the bitfield-backed sync
    /// in BackgroundTaskManager (libtorrent derives total_done in torrent::bytes_done()
    /// from the piece picker, never from a transfer counter). Writing the session
    /// counter here made the progress bar oscillate after fast resume.
    /// </summary>
    internal static void ApplyProgressEventStatistics(
        TorrentStatistics stats, vTorrent.Abstractions.Events.TorrentProgressEventArgs e)
    {
        stats.PiecesCompleted = e.PiecesCompleted;
        stats.DownloadRate = (int)e.DownloadRate;
        stats.UploadRate = (int)e.UploadRate;
        stats.ConnectedPeers = e.ConnectedPeers;
        stats.ConnectedSeeds = e.ConnectedSeeds;
        stats.FailedBytes = e.FailedBytes;
    }

    private async Task SaveTorrentStateAsync(ManagedTorrent managed)

    {

        try

        {

            UpdateResumeDataFromTorrent(managed);

            await _persistence.SaveResumeDataAsync(managed.InfoHash, managed.ResumeData).ConfigureAwait(false);

            await _persistence.UpdateTorrentIntentAsync(managed.InfoHash, managed.GetStatus().Intent.ToString()).ConfigureAwait(false);

        }

        catch (Exception ex)

        {

            _logger.LogWarning(ex, "Failed to save state for {Name}", managed.Name);

        }

    }

    private async Task OnTorrentCompletedAsync(ManagedTorrent managed)

    {

        try

        {

            // Mark as completed in database (sets is_finished, is_seed, completed_at)

            await _persistence.MarkTorrentCompletedAsync(managed.InfoHash).ConfigureAwait(false);

            // Also save resume data (gated on AutoSave.SaveOnTorrentComplete)
            // RUNTIME: use monitor for live settings, fallback to persistence
            if ((_autoSaveMonitor?.CurrentValue ?? _persistence.Settings.AutoSave).SaveOnTorrentComplete)

            {

                UpdateResumeDataFromTorrent(managed);

                await _persistence.SaveResumeDataAsync(managed.InfoHash, managed.ResumeData).ConfigureAwait(false);

            }

            _logger.LogInformation("Torrent completed: {Name}", managed.Name);

        }

        catch (Exception ex)

        {

            _logger.LogWarning(ex, "Failed to mark torrent as completed: {Name}", managed.Name);

        }

    }

    /// <summary>

    /// Internal method to pause a torrent (called by auto-manager)

    /// </summary>

    /// <summary>
    /// Callback for DiskErrorRecoveryManager to retry a torrent after disk errors.
    /// Placeholder — full implementation requires finding the torrent and resuming.
    /// </summary>
    private async Task<bool> RetryErroredTorrentAsync(string infoHashHex)
    {
        _logger.LogDebug("Attempting disk error retry for {Hash}", infoHashHex);
        var managed = _torrents.Find(infoHashHex);
        if (managed?.Engine == null)
            return false;

        try
        {
            await _lifecycleManager.StartTorrentAsync(infoHashHex, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disk error retry failed for {Hash}", infoHashHex);
            return false;
        }
    }

    private bool PauseTorrentInternal(ManagedTorrent managed)

    {

        var currentStatus = managed.GetStatus();
        if (currentStatus.Intent == UserIntent.Paused || currentStatus.Intent == UserIntent.Queued)
            return false;

        try
        {
            _logger.LogDebug("Pausing engine for: {Name}", managed.Name);

            // Stop the engine asynchronously
            if (managed.Engine != null)
            {
                _ = StopEngineAsync(managed, CancellationToken.None);
            }

            var queuedStatus = currentStatus with { Intent = UserIntent.Queued, Phase = TransferPhase.Idle };
            UpdateStatus(managed, queuedStatus);

            _alertManager.Post(new TorrentPausedAlert(managed.InfoHash, userInitiated: false));

            return true;

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Failed to pause engine for {Name}", managed.Name);

            return false;

        }

    }

    /// <summary>
    /// Called when VPN settings change at runtime. Restarts or disables the kill-switch
    /// to reflect the new configuration without requiring a full session restart.
    /// </summary>
    private void OnVpnSettingsChanged(VpnSettings newSettings)
    {
        var vpnInterface = !string.IsNullOrWhiteSpace(newSettings.VpnInterfaceName)
            ? newSettings.VpnInterfaceName
            : (_connectionMonitor?.CurrentValue ?? _persistence.Settings.Connection).OutgoingInterface;

        if (newSettings.KillSwitchEnabled && !string.IsNullOrWhiteSpace(vpnInterface))
        {
            if (_vpnKillSwitch != null)
            {
                // Only restart if the interface name actually changed
                if (string.Equals(_vpnKillSwitch.MonitoredInterface, vpnInterface, StringComparison.OrdinalIgnoreCase))
                {
                    return; // Same interface, nothing to do
                }

                // Interface changed — restart monitoring
                _vpnKillSwitch.Stop();
                _vpnKillSwitch.Start(vpnInterface);
                _logger.LogInformation("VPN kill-switch restarted on interface '{Interface}'", vpnInterface);
            }
            else
            {
                // Kill-switch was off, now on
                _vpnKillSwitch = new VpnKillSwitch(_loggerFactory.CreateLogger<VpnKillSwitch>());
                _vpnKillSwitch.BlockingStateChanged += OnKillSwitchBlockingStateChanged;
                _vpnKillSwitch.Start(vpnInterface);
                _logger.LogInformation("VPN kill-switch enabled on interface '{Interface}'", vpnInterface);
            }
        }
        else
        {
            if (_vpnKillSwitch != null)
            {
                // Kill-switch was on, now off — clear all VPN-blocked flags and resume
                foreach (var managed in _torrents)
                {
                    if (managed.IsVpnBlocked)
                    {
                        managed.IsVpnBlocked = false;
                        StartTorrentInternal(managed);
                    }
                }

                _vpnKillSwitch.BlockingStateChanged -= OnKillSwitchBlockingStateChanged;
                _vpnKillSwitch.Dispose();
                _vpnKillSwitch = null;
                _logger.LogInformation("VPN kill-switch disabled");
            }
        }

        FireVpnStatusChanged();
    }

    /// <summary>
    /// VPN kill-switch callback. Stops all engines and UDP when VPN goes down,
    /// restarts UDP and lets auto-management resume torrents when VPN comes back.
    /// </summary>
    private void OnKillSwitchBlockingStateChanged(bool isBlocking)
    {
        if (isBlocking)
        {
            _logger.LogWarning("[VPN_KILLSWITCH] Blocking triggered — stopping all torrent engines and UDP socket");

            // Stop all running engines and mark them as VPN-blocked
            foreach (var managed in _torrents)
            {
                var status = managed.GetStatus();
                if (managed.Engine != null && status.Intent != UserIntent.Paused && status.Intent != UserIntent.Queued)
                {
                    managed.IsVpnBlocked = true;
                    PauseTorrentInternal(managed);
                    _logger.LogDebug("[VPN_KILLSWITCH] Blocked torrent: {Name}", managed.Name);
                }
            }

            // Stop UDP socket manager (uTP + DHT)
            if (_udpSocketManager != null)
            {
                _ = _udpSocketManager.StopAsync();
            }

            FireVpnStatusChanged();
        }
        else
        {
            _logger.LogInformation("[VPN_KILLSWITCH] Unblocking — re-binding sockets and resuming blocked torrents");

            // Re-resolve bind address from the recovered interface
            var vpnSettings = _vpnMonitor?.CurrentValue ?? _persistence.Settings.Vpn;
            var vpnInterface = !string.IsNullOrWhiteSpace(vpnSettings.VpnInterfaceName)
                ? vpnSettings.VpnInterfaceName
                : (_connectionMonitor?.CurrentValue ?? _persistence.Settings.Connection).OutgoingInterface;
            var bindAddress = InterfaceResolver.Resolve(vpnInterface);

            if (bindAddress == null)
            {
                // VPN interface is up but doesn't have an IP yet — skip rebind.
                // The next poll cycle (30s) will detect the interface is fully up and retry.
                _logger.LogWarning("[VPN_KILLSWITCH] Interface '{Interface}' is up but has no IP yet — deferring resume", vpnInterface);
                FireVpnStatusChanged();
                return;
            }

            // Re-bind UDP socket to the VPN interface specifically (not 0.0.0.0)
            if (_udpSocketManager != null)
            {
                var listenPort = (_connectionMonitor?.CurrentValue ?? _persistence.Settings.Connection).ListenPort;
                var proxySettings = _proxyMonitor?.CurrentValue ?? _persistence.Settings.Proxy;
                _ = _udpSocketManager.StartAsync(
                    new System.Net.IPEndPoint(bindAddress, listenPort),
                    CancellationToken.None,
                    proxySettings);
            }

            // Resume all VPN-blocked torrents
            foreach (var managed in _torrents)
            {
                if (managed.IsVpnBlocked)
                {
                    managed.IsVpnBlocked = false;
                    _logger.LogInformation("[VPN_KILLSWITCH] Resuming torrent: {Name}", managed.Name);
                    StartTorrentInternal(managed);
                }
            }

            FireVpnStatusChanged();
        }
    }

    #region IVpnStatusService

    bool IVpnStatusService.IsEnabled => _vpnKillSwitch != null;
    bool IVpnStatusService.IsMonitoring => _vpnKillSwitch?.IsMonitoring ?? false;
    bool IVpnStatusService.IsBlocking => _vpnKillSwitch?.IsBlocking ?? false;
    string IVpnStatusService.MonitoredInterface => _vpnKillSwitch?.MonitoredInterface ?? "";

    private event Action<VpnStatusInfo>? _vpnStatusChanged;
    event Action<VpnStatusInfo>? IVpnStatusService.StatusChanged
    {
        add => _vpnStatusChanged += value;
        remove => _vpnStatusChanged -= value;
    }

    private void FireVpnStatusChanged()
    {
        _vpnStatusChanged?.Invoke(new VpnStatusInfo(
            _vpnKillSwitch != null,
            _vpnKillSwitch?.IsMonitoring ?? false,
            _vpnKillSwitch?.IsBlocking ?? false,
            _vpnKillSwitch?.MonitoredInterface ?? ""));
    }

    #endregion

    internal async Task StopEngineAsync(ManagedTorrent managed, CancellationToken cancellationToken)

    {

        if (managed.Engine == null)

            return;

        try

        {

            managed.IsStopping = true;

            _logger.LogDebug("Stopping engine for: {Name}", managed.Name);

            // Capture stats before stopping

            managed.Statistics.DownloadRate = 0;

            managed.Statistics.UploadRate = 0;

            managed.Statistics.PiecesCompleted = managed.Engine.PiecesCompleted;

            // Graceful stop with timeout

            using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            stopCts.CancelAfter(TimeSpan.FromSeconds(5));

            try

            {

                // RUNTIME: use monitor for live settings, fallback to persistence
                var trackerTimeout = (_trackerMonitor?.CurrentValue ?? _persistence.Settings.Tracker).StopTrackerTimeout;

                await managed.Engine.StopAsync(stopCts.Token, trackerTimeout).ConfigureAwait(false);

            }

            catch (OperationCanceledException)

            {

                _logger.LogWarning("Engine stop timed out for {Name}", managed.Name);

            }

            // Dispose engine with timeout — Dispose() internally blocks on async disk backend
            // disposal which can hang if file handles or background tasks are stuck.
            // libtorrent pattern: remove_torrent returns fast, cleanup is async.

            var engine = managed.Engine;

            managed.Engine = null;

            try

            {

                await Task.Run(() => engine.Dispose())
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);

            }

            catch (TimeoutException)

            {

                _logger.LogWarning(
                    "Engine dispose timed out for {Name} — file handles may leak until process exit",
                    managed.Name);

            }

        }

        catch (Exception ex)

        {

            _logger.LogWarning(ex, "Error stopping engine for {Name}", managed.Name);

            managed.Engine = null;

        }

        finally

        {

            managed.IsStopping = false;

        }

    }

    private async Task StopAllEnginesAsync(CancellationToken cancellationToken)

    {

        var activeTorrents = _torrents.Where(t => t.Engine != null).ToList();

        var stopTasks = activeTorrents.Select(t => StopEngineAsync(t, cancellationToken));

        await Task.WhenAll(stopTasks).ConfigureAwait(false);

    }

    #endregion

    #region State Management

    /// <summary>

    /// Gets the state a torrent should resume to on next startup.

    /// Maps current states to their resumable equivalents.

    /// </summary>

    private static string GetResumableState(ManagedTorrent torrent)

    {

        // If user explicitly paused, preserve that

        if (torrent.UserPaused)

            return "paused";

        // Map current status to resumable state string
        var status = torrent.GetStatus();

        if (status.Error.HasValue)
            return "error";

        return status.Intent switch
        {
            UserIntent.Paused => "paused",
            UserIntent.Queued => "queued",
            UserIntent.Active => status.Phase switch
            {
                TransferPhase.Downloading => "downloading",
                TransferPhase.Seeding => "seeding",
                _ => "queued"  // Allocating, Checking, Connecting, Idle, Stopping — will restart
            },
            _ => "paused"  // Unknown - safe default
        };

    }

    internal void UpdateStatus(ManagedTorrent managed, TorrentStatus newStatus)
    {
        // Transitional shim — forwards to StateController via PostRestore.
        // StateIndex update, statistics mirror, event firing, persistence, and auto-manager
        // trigger are now handled by the StatusChanged / ChannelDrained subscriptions
        // wired in WireEngineEvents(). Will be removed when all callers post triggers directly.
        managed.UpdateStatus(newStatus, force: true);
    }

    /// <summary>

    /// Merge an engine status update into a managed torrent's status.

    /// Engine only touches its owned dimensions (Phase, FileOp, metrics).

    /// Intent, Health, IsAutoManaged are preserved from current status.

    /// </summary>

    /// <summary>

    /// Pull model — returns all torrents whose status changed since last call.

    /// For WebUI polling over WebSocket.

    /// </summary>

    public IReadOnlyList<(string InfoHash, TorrentStatus Status)> GetChangedTorrents()

    {

        var changed = new List<(string, TorrentStatus)>();

        foreach (var managed in _torrents)

        {

            if (managed.TryConsumeDirtyFlag())

                changed.Add((managed.InfoHash, managed.GetStatus()));

        }

        return changed;

    }

    /// <summary>

    /// Asynchronously persist state change to database.

    /// Fire-and-forget pattern with error logging - state is already in memory,

    /// this just ensures persistence for crash recovery.

    /// </summary>

    private async Task PersistStateChangeAsync(ManagedTorrent managed, string state)

    {

        try

        {

            await _persistence.UpdateTorrentIntentAsync(managed.InfoHash, state).ConfigureAwait(false);

        }

        catch (Exception ex)

        {

            _logger.LogWarning(ex, "Failed to persist state change for {Name} to {State}",

                managed.Name, state);

        }

    }

    #endregion

    #region Restore

    private async Task RestoreTorrentAsync(
        TorrentRecord record,
        Dictionary<int, Category> allCategories,
        Dictionary<string, List<Tag>> allTagsMapping,
        CancellationToken cancellationToken)

    {

        _logger.LogDebug("Restoring torrent: {Name} ({InfoHash})", record.Name, record.InfoHash);

        // Load resume data

        var resumeData = await _persistence.LoadResumeDataAsync(record.InfoHash).ConfigureAwait(false);

        // Create managed torrent

        var managed = ManagedTorrent.FromRecord(record, resumeData);

        // Reconstruct TorrentStatus from persisted state (before any other processing)

        var reconstructed = ReconstructOnStartup(managed);

        managed.ForceStatus(reconstructed, "startup: reconstructed from persistence", _logger);

        ResolvePendingMove(managed);

        // Load category name if category is set

        if (managed.CategoryId.HasValue && allCategories.TryGetValue(managed.CategoryId.Value, out var category))
        {
            managed.CategoryName = category.Name;
        }

        // Load tags for this torrent

        managed.Tags = allTagsMapping.TryGetValue(record.InfoHash, out var tags) ? tags : new List<Tag>();

        // === Load torrent metadata ===
        // Priority: (1) embedded bytes from resume data (no disk I/O)
        //           (2) stored .torrent file path
        //           (3) fallback persistent location
        bool loadedFromEmbedded = false;

        if (resumeData?.TorrentFileBytes != null && resumeData.TorrentFileBytes.Length > 0)
        {
            try
            {
                var parser = new BencodeParser();
                var parsed = parser.Parse(resumeData.TorrentFileBytes, out _);

                if (parsed is BDictionary dict)
                {
                    managed.Torrent = TorrentParser.FromBDictionary(dict);
                    loadedFromEmbedded = true;
                    Interlocked.Increment(ref _embeddedRestoreCount);

                    // Still record the torrent file path for reference
                    if (!string.IsNullOrEmpty(record.TorrentFilePath))
                        managed.TorrentFilePath = record.TorrentFilePath;
                    else
                    {
                        var persistentPath = Path.Combine(_persistence.DataDirectory, "torrents", $"{record.InfoHash}.torrent");
                        if (File.Exists(persistentPath))
                            managed.TorrentFilePath = persistentPath;
                    }

                    _logger.LogDebug("Loaded torrent metadata from embedded resume data for {Name}", record.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse embedded torrent bytes for {InfoHash}, falling back to disk", record.InfoHash);
            }
        }

        if (!loadedFromEmbedded)
        {
            // Fallback: read .torrent file from disk (backward compat with old resume data)
            string? torrentFilePath = null;

            if (!string.IsNullOrEmpty(record.TorrentFilePath) && File.Exists(record.TorrentFilePath))
            {
                torrentFilePath = record.TorrentFilePath;
            }
            else
            {
                var persistentPath = Path.Combine(_persistence.DataDirectory, "torrents", $"{record.InfoHash}.torrent");
                if (File.Exists(persistentPath))
                {
                    torrentFilePath = persistentPath;
                    _logger.LogDebug("Using fallback torrent file location: {Path}", persistentPath);
                }
            }

            if (torrentFilePath != null)
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(torrentFilePath, cancellationToken).ConfigureAwait(false);
                    var parser = new BencodeParser();
                    var parsed = parser.Parse(bytes, out _);

                    if (parsed is BDictionary dict)
                    {
                        managed.Torrent = TorrentParser.FromBDictionary(dict);
                        managed.TorrentFilePath = torrentFilePath;
                        Interlocked.Increment(ref _diskFallbackRestoreCount);

                        // Backfill embedded bytes so next shutdown save has them
                        if (resumeData != null)
                            resumeData.TorrentFileBytes = bytes;
                    }
                    else
                    {
                        _logger.LogWarning("Failed to parse torrent file for {InfoHash}: invalid bencode", record.InfoHash);
                        managed.SetError("Failed to parse torrent file - invalid format");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load torrent file for {InfoHash}", record.InfoHash);
                    managed.SetError($"Failed to load torrent file: {ex.Message}");
                }
            }
            else
            {
                _logger.LogWarning("Torrent file not found for {Name} ({InfoHash}). Torrent cannot be started without metadata.",
                    record.Name, record.InfoHash);
                managed.SetError("Torrent file not found - cannot start without metadata");
            }
        }

        // Final check: if we still don't have torrent metadata, mark as error

        if (managed.Torrent == null && !managed.GetStatus().Error.HasValue)
        {
            managed.SetError("No torrent metadata available - cannot start");
        }

        // BEP 52: Load merkle trees for v2/hybrid torrents

        if (managed.Torrent != null)

        {

            managed.MerkleTrees = await _lifecycleManager.LoadOrBuildMerkleTreesAsync(

                managed.Torrent, managed.InfoHash, cancellationToken).ConfigureAwait(false);

        }

        // Add to runtime collections

        _torrents.Add(managed);

        _stateIndex.Add(managed);

        _queue.Add(managed);

        // Set appropriate state for auto-management

        // IMPORTANT: Don't override Error state - torrent can't be started without metadata

        // Following libtorrent's model: torrents that were active get queued for auto-management

        var restoreStatus = managed.GetStatus();
        if (!restoreStatus.Error.HasValue)
        {
            // Intents that should auto-start: Active (was downloading/seeding) or Queued
            var shouldAutoStart = record.UserIntent is "Active" or "Queued";

            if (shouldAutoStart)
            {
                // Was active or queued - queue for auto-management
                managed.UserPaused = false;
                var queuedStatus = restoreStatus with { Intent = UserIntent.Queued, Phase = TransferPhase.Idle };
                UpdateStatus(managed, queuedStatus);

                _logger.LogDebug("Restored torrent {Name} to Queued intent for auto-management (was: {OldIntent})",
                    managed.Name, record.UserIntent);
            }
            else if (record.UserIntent is "Paused")
            {
                // User explicitly paused - keep paused and mark UserPaused
                managed.UserPaused = true;
                var pausedStatus = restoreStatus with { Intent = UserIntent.Paused, Phase = TransferPhase.Idle };
                UpdateStatus(managed, pausedStatus);

                _logger.LogDebug("Restored torrent {Name} to Paused intent (user-paused)", managed.Name);
            }
            else
            {
                // Unexpected value (empty string, legacy phase name, null) — treat as Queued
                // This handles corrupted DB values from the Phase→Intent migration bug
                _logger.LogWarning("Restored torrent {Name} has unexpected UserIntent '{Intent}', treating as Queued",
                    managed.Name, record.UserIntent ?? "(null)");
                managed.UserPaused = false;
                var queuedStatus = restoreStatus with { Intent = UserIntent.Queued, Phase = TransferPhase.Idle };
                UpdateStatus(managed, queuedStatus);
            }
        }
        else
        {
            _logger.LogWarning("Torrent {Name} is in error state, cannot auto-start", managed.Name);
        }

        _logger.LogDebug("Restored torrent: {Name} (status: {Status})", managed.Name, managed.GetStatus());

    }

    /// <summary>

    /// Reconstruct TorrentStatus from persisted state on app startup.

    /// Rules: Phase→Idle, Active→Queued, permanent errors preserved, move recovery.

    /// </summary>

    private static TorrentStatus ReconstructOnStartup(ManagedTorrent managed)

    {

        var current = managed.GetStatus();

        // Rule 1: Phase always resets to Idle — engine re-derives on startup

        var phase = TransferPhase.Idle;

        // Rule 2: UserIntent preserved, but Active becomes Queued (auto-manager re-grants)

        var intent = current.Intent switch

        {

            UserIntent.Active => UserIntent.Queued,

            _ => current.Intent

        };

        // Also check the legacy UserPaused flag

        if (managed.UserPaused)

            intent = UserIntent.Paused;

        // Rule 3: FileOperation.Moving triggers recovery (handled separately)

        var fileOp = current.FileOp == FileOperation.Moving

            ? FileOperation.Moving  // Will be resolved by ResolvePendingMove

            : FileOperation.None;

        // Rule 4: Error/MissingFiles reset to null, except permanent errors

        TorrentError? persistedError = null;

        bool persistedMissing = false;

        if (IsPermanentError(current.Error, current.MissingFiles))

        {

            persistedError = current.Error;

            persistedMissing = current.MissingFiles;

        }

        return new TorrentStatus

        {

            Phase = phase,

            FileOp = fileOp,

            Intent = intent,

            Error = persistedError,

            MissingFiles = persistedMissing,

            IsAutoManaged = current.IsAutoManaged,

            IsFinished = current.IsFinished,

            IsSeed = current.IsSeed,

        };

    }

    private static bool IsPermanentError(TorrentError? error, bool missingFiles)

    {

        if (missingFiles) return true;

        if (!error.HasValue) return false;

        // Permanent errors: tracker banned, torrent removed

        var msg = error.Value.Message;

        return msg.Contains("banned", StringComparison.OrdinalIgnoreCase)

            || msg.Contains("not registered", StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>

    /// Resolve a pending move operation that was interrupted by crash.

    /// </summary>

    private void ResolvePendingMove(ManagedTorrent managed)

    {

        var status = managed.GetStatus();

        if (status.FileOp != FileOperation.Moving)

            return;

        // For now, just clear the Moving state — actual path verification

        // requires checking both source and target paths which depends on

        // the torrent's storage layout. Clear FileOp and let the user

        // re-initiate the move if needed.

        managed.ForceStatus(

            status with { FileOp = FileOperation.None },

            "startup: cleared pending move operation",

            _logger);

        _logger.LogDebug(

            "Cleared interrupted move operation for torrent {Name}", managed.Name);

    }

    #endregion

    #region Helpers

    /// <summary>

    /// Sync all relevant fields from ManagedTorrent to ResumeData before saving.

    /// Ensures complete state is preserved for proper resume.

    /// </summary>

    internal void UpdateResumeDataFromTorrent(ManagedTorrent torrent)

    {

        var resume = torrent.ResumeData;

        var stats = torrent.Statistics;

        // Persist time tracking stats

        resume.TotalUploaded = stats.AllTimeUploaded;

        resume.TotalDownloaded = stats.AllTimeDownloaded;

        resume.ActiveTimeSeconds = (long)stats.ActiveDuration.TotalSeconds;

        resume.SeedingTimeSeconds = (long)stats.SeedingDuration.TotalSeconds;

        resume.FinishedTimeSeconds = (long)stats.FinishedDuration.TotalSeconds;

        // Timestamps - last activity

        if (stats.LastDownload.HasValue)

            resume.LastDownload = new DateTimeOffset(stats.LastDownload.Value).ToUnixTimeSeconds();

        if (stats.LastUpload.HasValue)

            resume.LastUpload = new DateTimeOffset(stats.LastUpload.Value).ToUnixTimeSeconds();

        if (stats.CompletedTime.HasValue)

            resume.CompletedTime = new DateTimeOffset(stats.CompletedTime.Value).ToUnixTimeSeconds();

        // State flags

        var resumeStatus = torrent.GetStatus();
        resume.IsPaused = resumeStatus.Intent == UserIntent.Paused || resumeStatus.Intent == UserIntent.Queued;

        resume.UserPaused = torrent.UserPaused;

        resume.SequentialDownload = torrent.SequentialDownload;

        resume.AutoManaged = torrent.IsAutoManaged;

        // Queue position

        resume.QueuePosition = torrent.QueuePosition;

        // Swarm data if available (from tracker)

        resume.NumComplete = stats.TrackerSeeders;

        resume.NumIncomplete = stats.TrackerLeechers;

        // CRITICAL: Save the piece bitfield from the engine

        // This is what allows resuming downloads without re-downloading

        if (torrent.Engine != null)

        {

            var bitfield = torrent.Engine.GetPieceBitfield();

            if (bitfield != null)

            {

                // Ensure PieceCount is set for proper bitfield decoding on load

                if (resume.PieceCount == 0)

                    resume.PieceCount = bitfield.Length;

                resume.SetHavePieces(bitfield);

                var completedCount = 0;

                foreach (bool b in bitfield)

                    if (b) completedCount++;

                _logger.LogDebug("Saved bitfield for {Name}: {Completed}/{Total} pieces",

                    torrent.Name, completedCount, bitfield.Length);

            }

        }

        // Save seed mode verified pieces for resume
        if (torrent.Engine?.IsSeedMode == true && torrent.Engine.SeedModeVerifiedPieces != null)
        {
            var verifiedBitfield = torrent.Engine.SeedModeVerifiedPieces;
            var verifiedBitArray = new System.Collections.BitArray(verifiedBitfield.PieceCount);
            for (int i = 0; i < verifiedBitfield.PieceCount; i++)
            {
                if (verifiedBitfield.HasPiece(i))
                    verifiedBitArray.Set(i, true);
            }
            resume.VerifiedPieces = TorrentResumeData.BitArrayToBytesMsbFirst(verifiedBitArray);

            // Preserve SeedMode flag
            resume.Flags |= TorrentFlags.SeedMode;
        }

        // Sync file priorities from the running engine so they survive restart

        if (torrent.Engine != null)

        {

            var priorities = torrent.Engine.GetAllFilePriorities();

            if (priorities.Count > 0)

            {

                resume.FilePriorities ??= new Dictionary<int, int>();

                resume.FilePriorities.Clear();

                for (int i = 0; i < priorities.Count; i++)

                {

                    if (priorities[i] != FilePriority.Normal)

                        resume.FilePriorities[i] = (int)priorities[i];

                }

            }

        }

        // Embed .torrent file bytes for fast startup (libtorrent parity)
        // Only populate if not already set — avoids re-reading disk on every save
        // Cap at 1 MB to avoid bloating periodic resume saves for huge metadata files
        if (resume.TorrentFileBytes == null || resume.TorrentFileBytes.Length == 0)
        {
            var torrentPath = torrent.TorrentFilePath;
            if (string.IsNullOrEmpty(torrentPath))
                torrentPath = Path.Combine(_persistence.DataDirectory, "torrents", $"{torrent.InfoHash}.torrent");

            if (File.Exists(torrentPath))
            {
                try
                {
                    var fi = new FileInfo(torrentPath);
                    if (fi.Length <= TorrentResumeData.MaxEmbedTorrentFileSize)
                    {
                        resume.TorrentFileBytes = File.ReadAllBytes(torrentPath);
                    }
                    else
                    {
                        _logger.LogDebug("Torrent file too large to embed ({Size} bytes) for {Name}, startup will read from disk",
                            fi.Length, torrent.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not embed torrent file for {Name}, startup will fall back to disk read", torrent.Name);
                }
            }
        }

        // Timestamp for staleness detection

        resume.LastSaved = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    }

    private void UpdateSessionState()

    {

        if (_sessionState == null) return;

        // RUNTIME: use monitor for live settings, fallback to persistence
        _sessionState.ListenPort = (_connectionMonitor?.CurrentValue ?? _persistence.Settings.Connection).ListenPort;

        _sessionState.PersistentStats.TotalBytesDownloaded += _statistics.TotalBytesReceived;

        _sessionState.PersistentStats.TotalBytesUploaded += _statistics.TotalBytesSent;

        _sessionState.PersistentStats.TotalRunTimeSeconds += (long)Uptime.TotalSeconds;

        // BEP 24: Persist voter state
        var voterRecords = _externalIpVoter.ExportToRecords();
        _sessionState.ExternalIps.Clear();
        foreach (var r in voterRecords)
        {
            _sessionState.ExternalIps.Add(new ExternalIpRecord
            {
                Ip = r.Ip,
                VoteCount = r.VoteCount,
                LastSeen = r.LastSeenUnix,
                FirstSeen = r.LastSeenUnix,
                Source = "aggregated"
            });
        }

    }

    /// <summary>

    /// Save session state if it exists (used by BackgroundTaskManager for periodic saves).

    /// </summary>

    internal void SaveSessionStateIfNeeded()
    {
        _ = SaveSessionStateIfNeededAsync();
    }

    private async Task SaveSessionStateIfNeededAsync()

    {

        try

        {

            if (_sessionState != null)

            {

                UpdateSessionState();

                await _persistence.SaveSessionStateAsync(_sessionState).ConfigureAwait(false);

            }

        }

        catch (Exception ex)

        {

            // Best-effort save — log and continue
            _logger?.LogError(ex, "Error saving session state");

        }

    }

    #endregion

}
