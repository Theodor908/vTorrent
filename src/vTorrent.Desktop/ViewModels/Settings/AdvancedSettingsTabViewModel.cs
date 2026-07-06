using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using CommunityToolkit.Mvvm.ComponentModel;
using vTorrent.Abstractions.Interfaces;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Desktop.ViewModels.Settings;

/// <summary>
/// Advanced tab: ~130 settings organized into 10 collapsible accordion sections.
/// Covers connection, bandwidth, disk, peer engine, uTP, tracker, DHT,
/// privacy/proxy, web seeds, and auto-save/logging.
/// </summary>
public partial class AdvancedSettingsTabViewModel : SettingsTabViewModelBase
{
    public override string TabName => "Advanced";
    public override string TabIcon => "\uE87C";

    // ============================================================
    // Search filter (property only — filtering logic lives in XAML)
    // ============================================================

    [ObservableProperty]
    private string _searchFilter = string.Empty;

    // ============================================================
    // Section expand/collapse toggles
    // ============================================================

    [ObservableProperty]
    private bool _isConnectionSectionExpanded = true;

    [ObservableProperty]
    private bool _isBandwidthSectionExpanded;

    [ObservableProperty]
    private bool _isDiskSectionExpanded;

    [ObservableProperty]
    private bool _isPeerSectionExpanded;

    [ObservableProperty]
    private bool _isUtpSectionExpanded;

    [ObservableProperty]
    private bool _isTrackerSectionExpanded;

    [ObservableProperty]
    private bool _isDhtSectionExpanded;

    [ObservableProperty]
    private bool _isPrivacySectionExpanded;

    [ObservableProperty]
    private bool _isWebSeedSectionExpanded;

    [ObservableProperty]
    private bool _isAutoSaveLoggingSectionExpanded;

    // ============================================================
    // Section 1: Connection & Transport (~19 properties)
    // ============================================================

    [ObservableProperty]
    private int _maxHalfOpenConnections = 50;

    [ObservableProperty]
    private int _connectionSpeed = 30;

    [ObservableProperty]
    private int _announcePort;

    [ObservableProperty]
    private bool _enableOutgoingUtp = true;

    [ObservableProperty]
    private bool _enableIncomingUtp = true;

    [ObservableProperty]
    private bool _enableOutgoingTcp = true;

    [ObservableProperty]
    private bool _enableIncomingTcp = true;

    [ObservableProperty]
    private bool _listenSystemPortFallback = true;

    [ObservableProperty]
    private bool _enableIpNotifier = true;

    [ObservableProperty]
    private bool _allowMultipleConnectionsPerIp;

    [ObservableProperty]
    private bool _noConnectPrivilegedPorts;

    [ObservableProperty]
    private bool _smoothConnects = true;

    [ObservableProperty]
    private bool _allowIdna;

    [ObservableProperty]
    private int _lsdAnnounceInterval = 300;

    [ObservableProperty]
    private string _outgoingInterface = string.Empty;

    [ObservableProperty]
    private string _ipFilterFilePath = string.Empty;

    [ObservableProperty]
    private int _upnpLeaseSeconds = 3600;

    [ObservableProperty]
    private int _natPmpLeaseSeconds = 3600;

    [ObservableProperty]
    private bool _upnpIgnoreNonRouters;

    // ============================================================
    // Section 2: Bandwidth & Mixed Mode (~7 properties)
    // ============================================================

    [ObservableProperty]
    private bool _rateLimitIpOverhead;

    [ObservableProperty]
    private MixedModeAlgorithm _mixedModeAlgorithm = MixedModeAlgorithm.PeerProportional;

    [ObservableProperty]
    private int _inactiveDownRate = 2048;

    [ObservableProperty]
    private int _inactiveUpRate = 2048;

    [ObservableProperty]
    private int _autoManageInterval = 30;

    [ObservableProperty]
    private int _autoManageStartup = 60;

    [ObservableProperty]
    private int _connectSeedEveryNDownload = 10;

    /// <summary>Available MixedModeAlgorithm values for combo box binding.</summary>
    public ObservableCollection<MixedModeAlgorithm> MixedModeAlgorithmValues { get; } =
        new(Enum.GetValues<MixedModeAlgorithm>());

    // ============================================================
    // Section 3: Disk I/O (~21 properties)
    // ============================================================

    [ObservableProperty]
    private DiskBackendType _backendType = DiskBackendType.Auto;

    [ObservableProperty]
    private DiskIoMode _readMode = DiskIoMode.EnableOsCache;

    [ObservableProperty]
    private DiskIoMode _writeMode = DiskIoMode.EnableOsCache;

    [ObservableProperty]
    private int _mmapFileSizeCutoff = 40;

    /// <summary>Displayed/edited in MB; converted to/from bytes in Load/Apply.</summary>
    [ObservableProperty]
    private long _mmapMemoryCeilingMb = 4096;

    [ObservableProperty]
    private int _filePoolSize = 40;

    /// <summary>Displayed/edited in MB; converted to/from bytes in Load/Apply.</summary>
    [ObservableProperty]
    private long _cacheSizeMb = 64;

    [ObservableProperty]
    private int _maxOutstandingDiskRequests = 64;

    [ObservableProperty]
    private long _maxQueuedDiskBytes;

    [ObservableProperty]
    private int _checkingMemUsage = 256;

    [ObservableProperty]
    private int _hashThreads = 2;

    [ObservableProperty]
    private bool _noAtimeStorage = true;

    [ObservableProperty]
    private bool _disableHashChecks;

    [ObservableProperty]
    private bool _noRecheckIncompleteResume;

    [ObservableProperty]
    private bool _pieceExtentAffinity;

    [ObservableProperty]
    private int _pieceExtentSize = 4_194_304;

    [ObservableProperty]
    private int _closeFileInterval = -1;

    [ObservableProperty]
    private int _optimisticDiskRetry = 600;

    [ObservableProperty]
    private int _maxDiskRetries = 5;

    /// <summary>Displayed/edited in MB; converted to/from bytes in Load/Apply.</summary>
    [ObservableProperty]
    private long _diskSpaceWarningMb = 1024;

    /// <summary>Displayed/edited in MB; converted to/from bytes in Load/Apply.</summary>
    [ObservableProperty]
    private long _diskSpaceCriticalMb = 100;

    /// <summary>Available DiskBackendType values for combo box binding.</summary>
    public ObservableCollection<DiskBackendType> BackendTypeValues { get; } =
        new(Enum.GetValues<DiskBackendType>());

    /// <summary>Available DiskIoMode values for combo box binding.</summary>
    public ObservableCollection<DiskIoMode> DiskIoModeValues { get; } =
        new(Enum.GetValues<DiskIoMode>());

    // ============================================================
    // Section 4: Peer Engine (~36 properties)
    // ============================================================

    [ObservableProperty]
    private int _connectTimeout = 15;

    [ObservableProperty]
    private int _handshakeTimeout = 10;

    [ObservableProperty]
    private int _requestTimeout = 60;

    [ObservableProperty]
    private int _pieceTimeout = 20;

    [ObservableProperty]
    private int _inactivityTimeout = 600;

    [ObservableProperty]
    private int _metadataDownloadTimeoutMinutes = 10;

    [ObservableProperty]
    private ChokingAlgorithm _chokingAlgorithm = ChokingAlgorithm.RateBased;

    [ObservableProperty]
    private SeedChokingAlgorithm _seedChokingAlgorithm = SeedChokingAlgorithm.FastestUpload;

    [ObservableProperty]
    private int _unchokeSlots = 8;

    [ObservableProperty]
    private int _unchokeInterval = 15;

    [ObservableProperty]
    private int _optimisticUnchokeInterval = 30;

    [ObservableProperty]
    private int _numOptimisticUnchokeSlots;

    [ObservableProperty]
    private bool _sendRedundantHave = true;

    [ObservableProperty]
    private bool _useParoleMode = true;

    [ObservableProperty]
    private bool _seedingOutgoingConnections = true;

    [ObservableProperty]
    private bool _reportRedundantBytes = true;

    [ObservableProperty]
    private bool _reportTrueDownloaded;

    [ObservableProperty]
    private int _initialPickerThreshold = 4;

    [ObservableProperty]
    private int _wholePiecesThreshold = 20;

    [ObservableProperty]
    private int _maxPendingBlocksPerPeer = 500;

    [ObservableProperty]
    private int _peerTurnover = 4;

    [ObservableProperty]
    private int _peerTurnoverCutoff = 90;

    [ObservableProperty]
    private int _peerTurnoverInterval = 300;

    [ObservableProperty]
    private double _autoSequentialRatio = 0.8;

    [ObservableProperty]
    private bool _autoSequentialInSeederSwarm = true;

    [ObservableProperty]
    private bool _closeRedundantConnections = true;

    [ObservableProperty]
    private bool _prioritizePartialPieces;

    [ObservableProperty]
    private bool _strictEndgameMode = true;

    [ObservableProperty]
    private int _allowedFastSetSize = 5;

    [ObservableProperty]
    private int _maxRejects = 50;

    [ObservableProperty]
    private int _requestQueueTime = 3;

    /// <summary>Displayed/edited in MB; converted to/from bytes in Load/Apply.</summary>
    [ObservableProperty]
    private int _maxMetadataSizeMb = 30;

    [ObservableProperty]
    private int _maxPeerlistSize = 3000;

    [ObservableProperty]
    private int _peerDscp = 0x04;

    [ObservableProperty]
    private int _sendBufferWatermark;

    [ObservableProperty]
    private int _sendBufferLowWatermark = 10 * 1024;

    [ObservableProperty]
    private int _sendBufferWatermarkFactor = 50;

    /// <summary>Available ChokingAlgorithm values for combo box binding.</summary>
    public ObservableCollection<ChokingAlgorithm> ChokingAlgorithmValues { get; } =
        new(Enum.GetValues<ChokingAlgorithm>());

    /// <summary>Available SeedChokingAlgorithm values for combo box binding.</summary>
    public ObservableCollection<SeedChokingAlgorithm> SeedChokingAlgorithmValues { get; } =
        new(Enum.GetValues<SeedChokingAlgorithm>());

    // ============================================================
    // Section 5: uTP Tuning (~9 properties)
    // ============================================================

    [ObservableProperty]
    private int _utpTargetDelay = 100;

    [ObservableProperty]
    private int _utpGainFactor = 3000;

    [ObservableProperty]
    private int _utpMinTimeout = 500;

    [ObservableProperty]
    private int _utpSynResends = 2;

    [ObservableProperty]
    private int _utpFinResends = 2;

    [ObservableProperty]
    private int _utpNumResends = 3;

    [ObservableProperty]
    private int _utpLossMultiplier = 50;

    [ObservableProperty]
    private int _utpCwndReduceTimer = 100;

    [ObservableProperty]
    private int _utpConnectTimeoutMs = 5000;

    // ============================================================
    // Section 6: Tracker (~23 properties)
    // ============================================================

    [ObservableProperty]
    private bool _preferUdpTrackers = true;

    [ObservableProperty]
    private bool _announceCryptoSupport = true;

    [ObservableProperty]
    private bool _applyIpFilterToTrackers = true;

    [ObservableProperty]
    private bool _validateHttpsTrackers = true;

    [ObservableProperty]
    private bool _ssrfMitigation = true;

    [ObservableProperty]
    private bool _announceToAllTrackers;

    [ObservableProperty]
    private bool _announceToAllTiers;

    [ObservableProperty]
    private bool _parallelAnnounceAcrossTiers = true;

    [ObservableProperty]
    private bool _trackerReportRedundantBytes = true;

    [ObservableProperty]
    private bool _trackerReportTrueDownloaded;

    [ObservableProperty]
    private string _announceIp = string.Empty;

    [ObservableProperty]
    private int _minAnnounceInterval = 300;

    [ObservableProperty]
    private int _autoScrapeInterval = 1800;

    [ObservableProperty]
    private int _autoScrapeMinInterval = 300;

    [ObservableProperty]
    private int _trackerBackoff = 250;

    [ObservableProperty]
    private int _retryDelaySeconds = 5;

    [ObservableProperty]
    private int _maxConcurrentAnnounces = 10;

    [ObservableProperty]
    private int _maxParallelAnnounces = 10;

    [ObservableProperty]
    private int _stopTrackerTimeout = 5;

    [ObservableProperty]
    private int _httpTimeoutSeconds = 30;

    [ObservableProperty]
    private int _udpTimeoutSeconds = 15;

    [ObservableProperty]
    private int _maxRetries = 3;

    [ObservableProperty]
    private int _numWant = 200;

    // ============================================================
    // Section 7: DHT (~21 properties)
    // ============================================================

    [ObservableProperty]
    private int _dhtPort = 6881;

    [ObservableProperty]
    private int _searchBranching = 5;

    [ObservableProperty]
    private int _queryTimeoutMs = 5000;

    [ObservableProperty]
    private int _maxPeersReply = 100;

    [ObservableProperty]
    private int _maxPeersPerInfoHash = 500;

    [ObservableProperty]
    private int _maxInfoHashes = 2000;

    [ObservableProperty]
    private int _maxTotalPeers = 100_000;

    [ObservableProperty]
    private int _announceIntervalMs = 900_000;

    [ObservableProperty]
    private int _maxFailCount = 5;

    [ObservableProperty]
    private bool _enforceNodeId = true;

    [ObservableProperty]
    private bool _restrictRoutingIps = true;

    [ObservableProperty]
    private bool _extendedRoutingTable = true;

    [ObservableProperty]
    private bool _preferVerifiedNodeIds = true;

    [ObservableProperty]
    private bool _dhtReadOnly;

    /// <summary>Comma-separated bootstrap nodes; split/joined in Load/Apply.</summary>
    [ObservableProperty]
    private string _bootstrapNodes = string.Empty;

    [ObservableProperty]
    private int _maxSampleCount = 20;

    [ObservableProperty]
    private int _sampleInfohashesIntervalSeconds = 600;

    [ObservableProperty]
    private int _blockTimeoutSeconds = 300;

    [ObservableProperty]
    private int _uploadRateLimitBytesPerSec = 8000;

    [ObservableProperty]
    private int _blockRateLimitPacketsPerSec = 5;

    [ObservableProperty]
    private int _maxBlockedIps = 20;

    // ============================================================
    // Section 8: Privacy & Proxy (~23 properties)
    // ============================================================

    // -- Proxy --

    [ObservableProperty]
    private ProxyType _proxyType = ProxyType.None;

    [ObservableProperty]
    private string _proxyHostname = string.Empty;

    [ObservableProperty]
    private int _proxyPort;

    [ObservableProperty]
    private string _proxyUsername = string.Empty;

    [ObservableProperty]
    private string _proxyPassword = string.Empty;

    [ObservableProperty]
    private bool _proxyPeerConnections = true;

    [ObservableProperty]
    private bool _proxyTrackerConnections = true;

    [ObservableProperty]
    private bool _proxyDht;

    [ObservableProperty]
    private bool _proxyHostnames = true;

    // -- VPN --

    [ObservableProperty]
    private bool _killSwitchEnabled;

    [ObservableProperty]
    private string _vpnInterfaceName = string.Empty;

    /// <summary>Available network interfaces for VPN dropdown.</summary>
    public ObservableCollection<NetworkInterfaceInfo> AvailableInterfaces { get; } = new();

    [ObservableProperty]
    private NetworkInterfaceInfo? _selectedInterface;

    partial void OnSelectedInterfaceChanged(NetworkInterfaceInfo? value)
    {
        if (value != null)
            VpnInterfaceName = value.Name;
    }

    /// <summary>Refresh the available interfaces list.</summary>
    public void RefreshAvailableInterfaces()
    {
        AvailableInterfaces.Clear();

        // "Any Interface" sentinel
        AvailableInterfaces.Add(new NetworkInterfaceInfo
        {
            Name = "",
            Description = "Any Interface",
            IpAddress = "",
            IsUp = true
        });

        var interfaces = vTorrent.Core.Network.InterfaceResolver.GetAvailableInterfaces();
        foreach (var iface in interfaces)
        {
            AvailableInterfaces.Add(iface);
        }
    }

    // -- VPN Status Indicator --

    [ObservableProperty]
    private string _killSwitchStatusText = "Kill switch is disabled";

    [ObservableProperty]
    private Avalonia.Media.IBrush _killSwitchStatusBrush = Avalonia.Media.Brushes.Gray;

    private IVpnStatusService? _vpnStatusService;

    /// <summary>
    /// Initialize VPN status subscription. Called after DI is available.
    /// </summary>
    public void InitializeVpnStatus(IVpnStatusService? vpnStatusService)
    {
        _vpnStatusService = vpnStatusService;
        if (_vpnStatusService != null)
        {
            _vpnStatusService.StatusChanged += OnVpnStatusChanged;
            UpdateVpnStatusDisplay(new VpnStatusInfo(
                _vpnStatusService.IsEnabled,
                _vpnStatusService.IsMonitoring,
                _vpnStatusService.IsBlocking,
                _vpnStatusService.MonitoredInterface));
        }
    }

    private void OnVpnStatusChanged(VpnStatusInfo status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateVpnStatusDisplay(status));
    }

    private void UpdateVpnStatusDisplay(VpnStatusInfo status)
    {
        if (!status.IsEnabled)
        {
            KillSwitchStatusText = "Kill switch is disabled";
            KillSwitchStatusBrush = Avalonia.Media.Brushes.Gray;
        }
        else if (status.IsBlocking)
        {
            KillSwitchStatusText = $"'{status.MonitoredInterface}' is DOWN — all connections blocked";
            KillSwitchStatusBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ef4444"));
        }
        else if (status.IsMonitoring)
        {
            KillSwitchStatusText = $"Monitoring '{status.MonitoredInterface}' — connected";
            KillSwitchStatusBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#10b981"));
        }
        else
        {
            KillSwitchStatusText = "No interface selected";
            KillSwitchStatusBrush = Avalonia.Media.Brushes.Gray;
        }
    }

    // -- Network Change Subscription --

    private bool _networkChangeSubscribed;

    /// <summary>Call when Settings window opens.</summary>
    public void SubscribeNetworkChanges()
    {
        if (!_networkChangeSubscribed)
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            _networkChangeSubscribed = true;
        }
    }

    /// <summary>Call when Settings window closes.</summary>
    public void UnsubscribeNetworkChanges()
    {
        if (_networkChangeSubscribed)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            _networkChangeSubscribed = false;
        }

        if (_vpnStatusService != null)
        {
            _vpnStatusService.StatusChanged -= OnVpnStatusChanged;
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var currentSelection = VpnInterfaceName;
            RefreshAvailableInterfaces();
            SelectedInterface = AvailableInterfaces.FirstOrDefault(i => i.Name == currentSelection);
        });
    }

    // -- Privacy --

    [ObservableProperty]
    private bool _secureDeletion;

    [ObservableProperty]
    private bool _secureDeletionIncludeMetadata;

    // -- I2P --

    [ObservableProperty]
    private bool _i2pEnabled;

    [ObservableProperty]
    private string _samHostname = "127.0.0.1";

    [ObservableProperty]
    private int _samPort = 7656;

    [ObservableProperty]
    private int _inboundTunnelQuantity = 3;

    [ObservableProperty]
    private int _outboundTunnelQuantity = 3;

    [ObservableProperty]
    private int _inboundTunnelLength = 3;

    [ObservableProperty]
    private int _outboundTunnelLength = 3;

    [ObservableProperty]
    private I2pDestinationMode _destinationMode = I2pDestinationMode.Rotating;

    [ObservableProperty]
    private int _rotationIntervalDays = 7;

    [ObservableProperty]
    private bool _i2pAllowMixedMode;

    [ObservableProperty]
    private int _maxActiveI2pTorrents = 3;

    /// <summary>Available ProxyType values for combo box binding.</summary>
    public ObservableCollection<ProxyType> ProxyTypeValues { get; } =
        new(Enum.GetValues<ProxyType>());

    /// <summary>Available I2pDestinationMode values for combo box binding.</summary>
    public ObservableCollection<I2pDestinationMode> DestinationModeValues { get; } =
        new(Enum.GetValues<I2pDestinationMode>());

    // ============================================================
    // Section 9: Web Seeds (~6 properties)
    // ============================================================

    [ObservableProperty]
    private int _webSeedMaxConnections = 3;

    [ObservableProperty]
    private int _webSeedTimeout = 20;

    [ObservableProperty]
    private int _webSeedRetryWait = 30;

    /// <summary>Displayed/edited in MB; converted to/from bytes in Load/Apply.</summary>
    [ObservableProperty]
    private int _webSeedMaxRequestMb = 16;

    [ObservableProperty]
    private bool _alwaysSendUserAgent;

    [ObservableProperty]
    private bool _banWebSeeds = true;

    // ============================================================
    // Section 10: Auto-Save & Logging (~10 properties)
    // ============================================================

    [ObservableProperty]
    private bool _autoSaveEnabled = true;

    [ObservableProperty]
    private int _autoSaveIntervalMinutes = 15;

    [ObservableProperty]
    private bool _saveOnTorrentComplete = true;

    [ObservableProperty]
    private bool _saveOnPause = true;

    [ObservableProperty]
    private bool _saveOnResume = true;

    [ObservableProperty]
    private ObservableCollection<string> _logLevels = new()
    {
        "Trace", "Debug", "Information", "Warning", "Error", "Critical"
    };

    [ObservableProperty]
    private string _selectedLogLevel = "Information";

    [ObservableProperty]
    private bool _logToFile;

    [ObservableProperty]
    private string _logFilePath = string.Empty;

    [ObservableProperty]
    private int _maxLogFileSizeMb = 10;

    [ObservableProperty]
    private int _maxLogFiles = 5;

    // ============================================================
    // Load / Apply
    // ============================================================

    public override void LoadFromSettings(GlobalSettings settings)
    {
        // -- Section 1: Connection & Transport --
        MaxHalfOpenConnections = settings.Connection.MaxHalfOpenConnections;
        ConnectionSpeed = settings.Connection.ConnectionSpeed;
        AnnouncePort = settings.Connection.AnnouncePort;
        EnableOutgoingUtp = settings.Connection.EnableOutgoingUtp;
        EnableIncomingUtp = settings.Connection.EnableIncomingUtp;
        EnableOutgoingTcp = settings.Connection.EnableOutgoingTcp;
        EnableIncomingTcp = settings.Connection.EnableIncomingTcp;
        ListenSystemPortFallback = settings.Connection.ListenSystemPortFallback;
        EnableIpNotifier = settings.Connection.EnableIpNotifier;
        AllowMultipleConnectionsPerIp = settings.Connection.AllowMultipleConnectionsPerIp;
        NoConnectPrivilegedPorts = settings.Connection.NoConnectPrivilegedPorts;
        SmoothConnects = settings.Connection.SmoothConnects;
        AllowIdna = settings.Connection.AllowIdna;
        LsdAnnounceInterval = settings.Connection.LsdAnnounceInterval;
        OutgoingInterface = settings.Connection.OutgoingInterface;
        IpFilterFilePath = settings.Connection.IpFilterFilePath;
        UpnpLeaseSeconds = settings.Connection.UpnpLeaseSeconds;
        NatPmpLeaseSeconds = settings.Connection.NatPmpLeaseSeconds;
        UpnpIgnoreNonRouters = settings.Connection.UpnpIgnoreNonRouters;

        // -- Section 2: Bandwidth & Mixed Mode --
        RateLimitIpOverhead = settings.Bandwidth.RateLimitIpOverhead;
        MixedModeAlgorithm = settings.Bandwidth.MixedModeAlgorithm;
        InactiveDownRate = settings.Queue.InactiveDownRate;
        InactiveUpRate = settings.Queue.InactiveUpRate;
        AutoManageInterval = settings.Queue.AutoManageInterval;
        AutoManageStartup = settings.Queue.AutoManageStartup;
        ConnectSeedEveryNDownload = settings.Queue.ConnectSeedEveryNDownload;

        // -- Section 3: Disk I/O --
        BackendType = settings.Disk.BackendType;
        ReadMode = settings.Disk.ReadMode;
        WriteMode = settings.Disk.WriteMode;
        MmapFileSizeCutoff = settings.Disk.MmapFileSizeCutoff;
        MmapMemoryCeilingMb = settings.Disk.MmapMemoryCeiling / (1024 * 1024);
        FilePoolSize = settings.Disk.FilePoolSize;
        CacheSizeMb = settings.Disk.CacheSize / (1024 * 1024);
        MaxOutstandingDiskRequests = settings.Disk.MaxOutstandingDiskRequests;
        MaxQueuedDiskBytes = settings.Disk.MaxQueuedDiskBytes;
        CheckingMemUsage = settings.Disk.CheckingMemUsage;
        HashThreads = settings.Disk.HashThreads;
        NoAtimeStorage = settings.Disk.NoAtimeStorage;
        DisableHashChecks = settings.Disk.DisableHashChecks;
        NoRecheckIncompleteResume = settings.Disk.NoRecheckIncompleteResume;
        PieceExtentAffinity = settings.Disk.PieceExtentAffinity;
        PieceExtentSize = settings.Disk.PieceExtentSize;
        CloseFileInterval = settings.Disk.CloseFileInterval;
        OptimisticDiskRetry = settings.Disk.OptimisticDiskRetry;
        MaxDiskRetries = settings.Disk.MaxDiskRetries;
        DiskSpaceWarningMb = settings.Disk.DiskSpaceWarningBytes / (1024 * 1024);
        DiskSpaceCriticalMb = settings.Disk.DiskSpaceCriticalBytes / (1024 * 1024);

        // -- Section 4: Peer Engine --
        ConnectTimeout = settings.Peer.ConnectTimeout;
        HandshakeTimeout = settings.Peer.HandshakeTimeout;
        RequestTimeout = settings.Peer.RequestTimeout;
        PieceTimeout = settings.Peer.PieceTimeout;
        InactivityTimeout = settings.Peer.InactivityTimeout;
        MetadataDownloadTimeoutMinutes = settings.Behavior.MetadataDownloadTimeoutMinutes;
        ChokingAlgorithm = settings.Behavior.ChokingAlgorithm;
        SeedChokingAlgorithm = settings.Behavior.SeedChokingAlgorithm;
        UnchokeSlots = settings.Behavior.UnchokeSlots;
        UnchokeInterval = settings.Peer.UnchokeInterval;
        OptimisticUnchokeInterval = settings.Peer.OptimisticUnchokeInterval;
        NumOptimisticUnchokeSlots = settings.Peer.NumOptimisticUnchokeSlots;
        SendRedundantHave = settings.Behavior.SendRedundantHave;
        UseParoleMode = settings.Behavior.UseParoleMode;
        SeedingOutgoingConnections = settings.Behavior.SeedingOutgoingConnections;
        ReportRedundantBytes = settings.Behavior.ReportRedundantBytes;
        ReportTrueDownloaded = settings.Behavior.ReportTrueDownloaded;
        InitialPickerThreshold = settings.Behavior.InitialPickerThreshold;
        WholePiecesThreshold = settings.Behavior.WholePiecesThreshold;
        MaxPendingBlocksPerPeer = settings.Peer.MaxPendingBlocksPerPeer;
        PeerTurnover = settings.Behavior.PeerTurnover;
        PeerTurnoverCutoff = settings.Behavior.PeerTurnoverCutoff;
        PeerTurnoverInterval = settings.Behavior.PeerTurnoverInterval;
        AutoSequentialRatio = settings.Behavior.AutoSequentialRatio;
        AutoSequentialInSeederSwarm = settings.Behavior.AutoSequentialInSeederSwarm;
        CloseRedundantConnections = settings.Behavior.CloseRedundantConnections;
        PrioritizePartialPieces = settings.Behavior.PrioritizePartialPieces;
        StrictEndgameMode = settings.Behavior.StrictEndgameMode;
        AllowedFastSetSize = settings.Peer.AllowedFastSetSize;
        MaxRejects = settings.Peer.MaxRejects;
        RequestQueueTime = settings.Peer.RequestQueueTime;
        MaxMetadataSizeMb = settings.Peer.MaxMetadataSize / (1024 * 1024);
        MaxPeerlistSize = settings.Peer.MaxPeerlistSize;
        PeerDscp = settings.Peer.PeerDscp;
        SendBufferWatermark = settings.Peer.SendBufferWatermark;
        SendBufferLowWatermark = settings.Peer.SendBufferLowWatermark;
        SendBufferWatermarkFactor = settings.Peer.SendBufferWatermarkFactor;

        // -- Section 5: uTP Tuning --
        UtpTargetDelay = settings.Peer.UtpTargetDelay;
        UtpGainFactor = settings.Peer.UtpGainFactor;
        UtpMinTimeout = settings.Peer.UtpMinTimeout;
        UtpSynResends = settings.Peer.UtpSynResends;
        UtpFinResends = settings.Peer.UtpFinResends;
        UtpNumResends = settings.Peer.UtpNumResends;
        UtpLossMultiplier = settings.Peer.UtpLossMultiplier;
        UtpCwndReduceTimer = settings.Peer.UtpCwndReduceTimer;
        UtpConnectTimeoutMs = settings.Peer.UtpConnectTimeoutMs;

        // -- Section 6: Tracker --
        PreferUdpTrackers = settings.Tracker.PreferUdpTrackers;
        AnnounceCryptoSupport = settings.Tracker.AnnounceCryptoSupport;
        ApplyIpFilterToTrackers = settings.Tracker.ApplyIpFilterToTrackers;
        ValidateHttpsTrackers = settings.Tracker.ValidateHttpsTrackers;
        SsrfMitigation = settings.Tracker.SsrfMitigation;
        AnnounceToAllTrackers = settings.Tracker.AnnounceToAllTrackers;
        AnnounceToAllTiers = settings.Tracker.AnnounceToAllTiers;
        ParallelAnnounceAcrossTiers = settings.Tracker.ParallelAnnounceAcrossTiers;
        TrackerReportRedundantBytes = settings.Tracker.ReportRedundantBytes;
        TrackerReportTrueDownloaded = settings.Tracker.ReportTrueDownloaded;
        AnnounceIp = settings.Tracker.AnnounceIp;
        MinAnnounceInterval = settings.Tracker.MinAnnounceInterval;
        AutoScrapeInterval = settings.Tracker.AutoScrapeInterval;
        AutoScrapeMinInterval = settings.Tracker.AutoScrapeMinInterval;
        TrackerBackoff = settings.Tracker.TrackerBackoff;
        RetryDelaySeconds = settings.Tracker.RetryDelaySeconds;
        MaxConcurrentAnnounces = settings.Tracker.MaxConcurrentAnnounces;
        MaxParallelAnnounces = settings.Tracker.MaxParallelAnnounces;
        StopTrackerTimeout = settings.Tracker.StopTrackerTimeout;
        HttpTimeoutSeconds = settings.Tracker.HttpTimeoutSeconds;
        UdpTimeoutSeconds = settings.Tracker.UdpTimeoutSeconds;
        MaxRetries = settings.Tracker.MaxRetries;
        NumWant = settings.Tracker.NumWant;

        // -- Section 7: DHT --
        DhtPort = settings.Dht.Port;
        SearchBranching = settings.Dht.SearchBranching;
        QueryTimeoutMs = settings.Dht.QueryTimeoutMs;
        MaxPeersReply = settings.Dht.MaxPeersReply;
        MaxPeersPerInfoHash = settings.Dht.MaxPeersPerInfoHash;
        MaxInfoHashes = settings.Dht.MaxInfoHashes;
        MaxTotalPeers = settings.Dht.MaxTotalPeers;
        AnnounceIntervalMs = settings.Dht.AnnounceIntervalMs;
        MaxFailCount = settings.Dht.MaxFailCount;
        EnforceNodeId = settings.Dht.EnforceNodeId;
        RestrictRoutingIps = settings.Dht.RestrictRoutingIps;
        ExtendedRoutingTable = settings.Dht.ExtendedRoutingTable;
        PreferVerifiedNodeIds = settings.Dht.PreferVerifiedNodeIds;
        DhtReadOnly = settings.Dht.ReadOnly;
        BootstrapNodes = string.Join(", ", settings.Dht.BootstrapNodes ?? Array.Empty<string>());
        MaxSampleCount = settings.Dht.MaxSampleCount;
        SampleInfohashesIntervalSeconds = settings.Dht.SampleInfohashesIntervalSeconds;
        BlockTimeoutSeconds = settings.Dht.BlockTimeoutSeconds;
        UploadRateLimitBytesPerSec = settings.Dht.UploadRateLimitBytesPerSec;
        BlockRateLimitPacketsPerSec = settings.Dht.BlockRateLimitPacketsPerSec;
        MaxBlockedIps = settings.Dht.MaxBlockedIps;

        // -- Section 8: Privacy & Proxy --
        ProxyType = settings.Proxy.Type;
        ProxyHostname = settings.Proxy.Hostname;
        ProxyPort = settings.Proxy.Port;
        ProxyUsername = settings.Proxy.Username;
        ProxyPassword = settings.Proxy.Password;
        ProxyPeerConnections = settings.Proxy.ProxyPeerConnections;
        ProxyTrackerConnections = settings.Proxy.ProxyTrackerConnections;
        ProxyDht = settings.Proxy.ProxyDht;
        ProxyHostnames = settings.Proxy.ProxyHostnames;
        KillSwitchEnabled = settings.Vpn.KillSwitchEnabled;
        VpnInterfaceName = settings.Vpn.VpnInterfaceName;
        RefreshAvailableInterfaces();
        SelectedInterface = AvailableInterfaces.FirstOrDefault(i => i.Name == VpnInterfaceName);
        SecureDeletion = settings.Privacy.SecureDeletion;
        SecureDeletionIncludeMetadata = settings.Privacy.SecureDeletionIncludeMetadata;
        I2pEnabled = settings.I2p.Enabled;
        SamHostname = settings.I2p.SamHostname;
        SamPort = settings.I2p.SamPort;
        InboundTunnelQuantity = settings.I2p.InboundTunnelQuantity;
        OutboundTunnelQuantity = settings.I2p.OutboundTunnelQuantity;
        InboundTunnelLength = settings.I2p.InboundTunnelLength;
        OutboundTunnelLength = settings.I2p.OutboundTunnelLength;
        DestinationMode = settings.I2p.DestinationMode;
        RotationIntervalDays = settings.I2p.RotationIntervalDays;
        I2pAllowMixedMode = settings.I2p.AllowMixedMode;
        MaxActiveI2pTorrents = settings.I2p.MaxActiveI2pTorrents;

        // -- Section 9: Web Seeds --
        WebSeedMaxConnections = settings.WebSeed.MaxConnectionsPerTorrent;
        WebSeedTimeout = settings.WebSeed.TimeoutSeconds;
        WebSeedRetryWait = settings.WebSeed.WaitRetrySeconds;
        WebSeedMaxRequestMb = settings.WebSeed.MaxRequestBytes / (1024 * 1024);
        AlwaysSendUserAgent = settings.WebSeed.AlwaysSendUserAgent;
        BanWebSeeds = settings.WebSeed.BanWebSeeds;

        // -- Section 10: Auto-Save & Logging --
        AutoSaveEnabled = settings.AutoSave.Enabled;
        AutoSaveIntervalMinutes = settings.AutoSave.IntervalMinutes;
        SaveOnTorrentComplete = settings.AutoSave.SaveOnTorrentComplete;
        SaveOnPause = settings.AutoSave.SaveOnPause;
        SaveOnResume = settings.AutoSave.SaveOnResume;
        SelectedLogLevel = settings.Logging.Level;
        LogToFile = settings.Logging.LogToFile;
        LogFilePath = settings.Logging.LogFilePath;
        MaxLogFileSizeMb = (int)(settings.Logging.MaxLogFileSize / (1024 * 1024));
        MaxLogFiles = settings.Logging.MaxLogFiles;
    }

    public override void ApplyToSettings(GlobalSettings settings)
    {
        // -- Section 1: Connection & Transport --
        settings.Connection.MaxHalfOpenConnections = MaxHalfOpenConnections;
        settings.Connection.ConnectionSpeed = ConnectionSpeed;
        settings.Connection.AnnouncePort = AnnouncePort;
        settings.Connection.EnableOutgoingUtp = EnableOutgoingUtp;
        settings.Connection.EnableIncomingUtp = EnableIncomingUtp;
        settings.Connection.EnableOutgoingTcp = EnableOutgoingTcp;
        settings.Connection.EnableIncomingTcp = EnableIncomingTcp;
        settings.Connection.ListenSystemPortFallback = ListenSystemPortFallback;
        settings.Connection.EnableIpNotifier = EnableIpNotifier;
        settings.Connection.AllowMultipleConnectionsPerIp = AllowMultipleConnectionsPerIp;
        settings.Connection.NoConnectPrivilegedPorts = NoConnectPrivilegedPorts;
        settings.Connection.SmoothConnects = SmoothConnects;
        settings.Connection.AllowIdna = AllowIdna;
        settings.Connection.LsdAnnounceInterval = LsdAnnounceInterval;
        settings.Connection.OutgoingInterface = OutgoingInterface;
        settings.Connection.IpFilterFilePath = IpFilterFilePath;
        settings.Connection.UpnpLeaseSeconds = UpnpLeaseSeconds;
        settings.Connection.NatPmpLeaseSeconds = NatPmpLeaseSeconds;
        settings.Connection.UpnpIgnoreNonRouters = UpnpIgnoreNonRouters;

        // -- Section 2: Bandwidth & Mixed Mode --
        settings.Bandwidth.RateLimitIpOverhead = RateLimitIpOverhead;
        settings.Bandwidth.MixedModeAlgorithm = MixedModeAlgorithm;
        settings.Queue.InactiveDownRate = InactiveDownRate;
        settings.Queue.InactiveUpRate = InactiveUpRate;
        settings.Queue.AutoManageInterval = AutoManageInterval;
        settings.Queue.AutoManageStartup = AutoManageStartup;
        settings.Queue.ConnectSeedEveryNDownload = ConnectSeedEveryNDownload;

        // -- Section 3: Disk I/O --
        settings.Disk.BackendType = BackendType;
        settings.Disk.ReadMode = ReadMode;
        settings.Disk.WriteMode = WriteMode;
        settings.Disk.MmapFileSizeCutoff = MmapFileSizeCutoff;
        settings.Disk.MmapMemoryCeiling = MmapMemoryCeilingMb * 1024 * 1024;
        settings.Disk.FilePoolSize = FilePoolSize;
        settings.Disk.CacheSize = CacheSizeMb * 1024 * 1024;
        settings.Disk.MaxOutstandingDiskRequests = MaxOutstandingDiskRequests;
        settings.Disk.MaxQueuedDiskBytes = MaxQueuedDiskBytes;
        settings.Disk.CheckingMemUsage = CheckingMemUsage;
        settings.Disk.HashThreads = HashThreads;
        settings.Disk.NoAtimeStorage = NoAtimeStorage;
        settings.Disk.DisableHashChecks = DisableHashChecks;
        settings.Disk.NoRecheckIncompleteResume = NoRecheckIncompleteResume;
        settings.Disk.PieceExtentAffinity = PieceExtentAffinity;
        settings.Disk.PieceExtentSize = PieceExtentSize;
        settings.Disk.CloseFileInterval = CloseFileInterval;
        settings.Disk.OptimisticDiskRetry = OptimisticDiskRetry;
        settings.Disk.MaxDiskRetries = MaxDiskRetries;
        settings.Disk.DiskSpaceWarningBytes = DiskSpaceWarningMb * 1024 * 1024;
        settings.Disk.DiskSpaceCriticalBytes = DiskSpaceCriticalMb * 1024 * 1024;

        // -- Section 4: Peer Engine --
        settings.Peer.ConnectTimeout = ConnectTimeout;
        settings.Peer.HandshakeTimeout = HandshakeTimeout;
        settings.Peer.RequestTimeout = RequestTimeout;
        settings.Peer.PieceTimeout = PieceTimeout;
        settings.Peer.InactivityTimeout = InactivityTimeout;
        settings.Behavior.MetadataDownloadTimeoutMinutes = MetadataDownloadTimeoutMinutes;
        settings.Behavior.ChokingAlgorithm = ChokingAlgorithm;
        settings.Behavior.SeedChokingAlgorithm = SeedChokingAlgorithm;
        settings.Behavior.UnchokeSlots = UnchokeSlots;
        settings.Peer.UnchokeInterval = UnchokeInterval;
        settings.Peer.OptimisticUnchokeInterval = OptimisticUnchokeInterval;
        settings.Peer.NumOptimisticUnchokeSlots = NumOptimisticUnchokeSlots;
        settings.Behavior.SendRedundantHave = SendRedundantHave;
        settings.Behavior.UseParoleMode = UseParoleMode;
        settings.Behavior.SeedingOutgoingConnections = SeedingOutgoingConnections;
        settings.Behavior.ReportRedundantBytes = ReportRedundantBytes;
        settings.Behavior.ReportTrueDownloaded = ReportTrueDownloaded;
        settings.Behavior.InitialPickerThreshold = InitialPickerThreshold;
        settings.Behavior.WholePiecesThreshold = WholePiecesThreshold;
        settings.Peer.MaxPendingBlocksPerPeer = MaxPendingBlocksPerPeer;
        settings.Behavior.PeerTurnover = PeerTurnover;
        settings.Behavior.PeerTurnoverCutoff = PeerTurnoverCutoff;
        settings.Behavior.PeerTurnoverInterval = PeerTurnoverInterval;
        settings.Behavior.AutoSequentialRatio = AutoSequentialRatio;
        settings.Behavior.AutoSequentialInSeederSwarm = AutoSequentialInSeederSwarm;
        settings.Behavior.CloseRedundantConnections = CloseRedundantConnections;
        settings.Behavior.PrioritizePartialPieces = PrioritizePartialPieces;
        settings.Behavior.StrictEndgameMode = StrictEndgameMode;
        settings.Peer.AllowedFastSetSize = AllowedFastSetSize;
        settings.Peer.MaxRejects = MaxRejects;
        settings.Peer.RequestQueueTime = RequestQueueTime;
        settings.Peer.MaxMetadataSize = MaxMetadataSizeMb * 1024 * 1024;
        settings.Peer.MaxPeerlistSize = MaxPeerlistSize;
        settings.Peer.PeerDscp = PeerDscp;
        settings.Peer.SendBufferWatermark = SendBufferWatermark;
        settings.Peer.SendBufferLowWatermark = SendBufferLowWatermark;
        settings.Peer.SendBufferWatermarkFactor = SendBufferWatermarkFactor;

        // -- Section 5: uTP Tuning --
        settings.Peer.UtpTargetDelay = UtpTargetDelay;
        settings.Peer.UtpGainFactor = UtpGainFactor;
        settings.Peer.UtpMinTimeout = UtpMinTimeout;
        settings.Peer.UtpSynResends = UtpSynResends;
        settings.Peer.UtpFinResends = UtpFinResends;
        settings.Peer.UtpNumResends = UtpNumResends;
        settings.Peer.UtpLossMultiplier = UtpLossMultiplier;
        settings.Peer.UtpCwndReduceTimer = UtpCwndReduceTimer;
        settings.Peer.UtpConnectTimeoutMs = UtpConnectTimeoutMs;

        // -- Section 6: Tracker --
        settings.Tracker.PreferUdpTrackers = PreferUdpTrackers;
        settings.Tracker.AnnounceCryptoSupport = AnnounceCryptoSupport;
        settings.Tracker.ApplyIpFilterToTrackers = ApplyIpFilterToTrackers;
        settings.Tracker.ValidateHttpsTrackers = ValidateHttpsTrackers;
        settings.Tracker.SsrfMitigation = SsrfMitigation;
        settings.Tracker.AnnounceToAllTrackers = AnnounceToAllTrackers;
        settings.Tracker.AnnounceToAllTiers = AnnounceToAllTiers;
        settings.Tracker.ParallelAnnounceAcrossTiers = ParallelAnnounceAcrossTiers;
        settings.Tracker.ReportRedundantBytes = TrackerReportRedundantBytes;
        settings.Tracker.ReportTrueDownloaded = TrackerReportTrueDownloaded;
        settings.Tracker.AnnounceIp = AnnounceIp;
        settings.Tracker.MinAnnounceInterval = MinAnnounceInterval;
        settings.Tracker.AutoScrapeInterval = AutoScrapeInterval;
        settings.Tracker.AutoScrapeMinInterval = AutoScrapeMinInterval;
        settings.Tracker.TrackerBackoff = TrackerBackoff;
        settings.Tracker.RetryDelaySeconds = RetryDelaySeconds;
        settings.Tracker.MaxConcurrentAnnounces = MaxConcurrentAnnounces;
        settings.Tracker.MaxParallelAnnounces = MaxParallelAnnounces;
        settings.Tracker.StopTrackerTimeout = StopTrackerTimeout;
        settings.Tracker.HttpTimeoutSeconds = HttpTimeoutSeconds;
        settings.Tracker.UdpTimeoutSeconds = UdpTimeoutSeconds;
        settings.Tracker.MaxRetries = MaxRetries;
        settings.Tracker.NumWant = NumWant;

        // -- Section 7: DHT --
        settings.Dht.Port = DhtPort;
        settings.Dht.SearchBranching = SearchBranching;
        settings.Dht.QueryTimeoutMs = QueryTimeoutMs;
        settings.Dht.MaxPeersReply = MaxPeersReply;
        settings.Dht.MaxPeersPerInfoHash = MaxPeersPerInfoHash;
        settings.Dht.MaxInfoHashes = MaxInfoHashes;
        settings.Dht.MaxTotalPeers = MaxTotalPeers;
        settings.Dht.AnnounceIntervalMs = AnnounceIntervalMs;
        settings.Dht.MaxFailCount = MaxFailCount;
        settings.Dht.EnforceNodeId = EnforceNodeId;
        settings.Dht.RestrictRoutingIps = RestrictRoutingIps;
        settings.Dht.ExtendedRoutingTable = ExtendedRoutingTable;
        settings.Dht.PreferVerifiedNodeIds = PreferVerifiedNodeIds;
        settings.Dht.ReadOnly = DhtReadOnly;
        settings.Dht.BootstrapNodes = BootstrapNodes
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        settings.Dht.MaxSampleCount = MaxSampleCount;
        settings.Dht.SampleInfohashesIntervalSeconds = SampleInfohashesIntervalSeconds;
        settings.Dht.BlockTimeoutSeconds = BlockTimeoutSeconds;
        settings.Dht.UploadRateLimitBytesPerSec = UploadRateLimitBytesPerSec;
        settings.Dht.BlockRateLimitPacketsPerSec = BlockRateLimitPacketsPerSec;
        settings.Dht.MaxBlockedIps = MaxBlockedIps;

        // -- Section 8: Privacy & Proxy --
        settings.Proxy.Type = ProxyType;
        settings.Proxy.Hostname = ProxyHostname;
        settings.Proxy.Port = ProxyPort;
        settings.Proxy.Username = ProxyUsername;
        settings.Proxy.Password = ProxyPassword;
        settings.Proxy.ProxyPeerConnections = ProxyPeerConnections;
        settings.Proxy.ProxyTrackerConnections = ProxyTrackerConnections;
        settings.Proxy.ProxyDht = ProxyDht;
        settings.Proxy.ProxyHostnames = ProxyHostnames;
        settings.Vpn.KillSwitchEnabled = KillSwitchEnabled;
        settings.Vpn.VpnInterfaceName = VpnInterfaceName;
        settings.Privacy.SecureDeletion = SecureDeletion;
        settings.Privacy.SecureDeletionIncludeMetadata = SecureDeletionIncludeMetadata;
        settings.I2p.Enabled = I2pEnabled;
        settings.I2p.SamHostname = SamHostname;
        settings.I2p.SamPort = SamPort;
        settings.I2p.InboundTunnelQuantity = InboundTunnelQuantity;
        settings.I2p.OutboundTunnelQuantity = OutboundTunnelQuantity;
        settings.I2p.InboundTunnelLength = InboundTunnelLength;
        settings.I2p.OutboundTunnelLength = OutboundTunnelLength;
        settings.I2p.DestinationMode = DestinationMode;
        settings.I2p.RotationIntervalDays = RotationIntervalDays;
        settings.I2p.AllowMixedMode = I2pAllowMixedMode;
        settings.I2p.MaxActiveI2pTorrents = MaxActiveI2pTorrents;

        // -- Section 9: Web Seeds --
        settings.WebSeed.MaxConnectionsPerTorrent = WebSeedMaxConnections;
        settings.WebSeed.TimeoutSeconds = WebSeedTimeout;
        settings.WebSeed.WaitRetrySeconds = WebSeedRetryWait;
        settings.WebSeed.MaxRequestBytes = WebSeedMaxRequestMb * 1024 * 1024;
        settings.WebSeed.AlwaysSendUserAgent = AlwaysSendUserAgent;
        settings.WebSeed.BanWebSeeds = BanWebSeeds;

        // -- Section 10: Auto-Save & Logging --
        settings.AutoSave.Enabled = AutoSaveEnabled;
        settings.AutoSave.IntervalMinutes = AutoSaveIntervalMinutes;
        settings.AutoSave.SaveOnTorrentComplete = SaveOnTorrentComplete;
        settings.AutoSave.SaveOnPause = SaveOnPause;
        settings.AutoSave.SaveOnResume = SaveOnResume;
        settings.Logging.Level = SelectedLogLevel;
        settings.Logging.LogToFile = LogToFile;
        settings.Logging.LogFilePath = LogFilePath;
        settings.Logging.MaxLogFileSize = MaxLogFileSizeMb * 1024L * 1024L;
        settings.Logging.MaxLogFiles = MaxLogFiles;
    }

    /// <summary>
    /// Set the log file path (called from view after folder selection).
    /// </summary>
    public void SetLogFilePath(string path)
    {
        if (!string.IsNullOrEmpty(path))
            LogFilePath = path;
    }
}
