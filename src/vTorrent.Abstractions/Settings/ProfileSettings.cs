using System;
using System.Text.Json.Serialization;
using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Abstractions.Settings;

/// <summary>
/// A named profile capturing ~35 performance-related settings as a preset.
/// </summary>
public class ProfileSettings
{
    /// <summary>Profile display name (unique identifier).</summary>
    public string Name { get; set; } = "";

    /// <summary>Profile accent color as hex string (e.g. "#2196F3").</summary>
    public string Color { get; set; } = "#2196F3";

    /// <summary>Profile scope. Currently only "performance" is supported.</summary>
    public string Scope { get; set; } = "performance";

    /// <summary>The 35 captured performance settings values.</summary>
    public ProfileSettingsValues Settings { get; set; } = new();
}

/// <summary>
/// The 35 performance-related settings values captured by a profile.
/// Maps to specific properties across GlobalSettings sub-classes.
/// </summary>
public class ProfileSettingsValues
{
    // === Bandwidth (5) ===

    /// <summary>Global download rate limit in bytes/s (0 = unlimited). Source: BandwidthSettings.</summary>
    public int GlobalDownloadLimit { get; set; } = 0;

    /// <summary>Global upload rate limit in bytes/s (0 = unlimited). Source: BandwidthSettings.</summary>
    public int GlobalUploadLimit { get; set; } = 0;

    /// <summary>Per-torrent download limit in bytes/s (0 = unlimited). Source: BandwidthSettings.</summary>
    public int PerTorrentDownloadLimit { get; set; } = 0;

    /// <summary>Per-torrent upload limit in bytes/s (0 = unlimited). Source: BandwidthSettings.</summary>
    public int PerTorrentUploadLimit { get; set; } = 0;

    /// <summary>TCP/uTP bandwidth sharing strategy. Source: BandwidthSettings.</summary>
    public MixedModeAlgorithm MixedModeAlgorithm { get; set; } = MixedModeAlgorithm.PeerProportional;

    // === Connection (5) ===

    /// <summary>Maximum global connections across all torrents. Source: ConnectionSettings.</summary>
    public int MaxGlobalConnections { get; set; } = 500;

    /// <summary>Maximum connections per torrent. Source: ConnectionSettings.</summary>
    public int MaxConnectionsPerTorrent { get; set; } = 200;

    /// <summary>Maximum upload slots per torrent. Source: ConnectionSettings.</summary>
    public int MaxUploadsPerTorrent { get; set; } = 4;

    /// <summary>Maximum half-open connections. Source: ConnectionSettings.</summary>
    public int MaxHalfOpenConnections { get; set; } = 50;

    /// <summary>Connection attempts per second per torrent. Source: ConnectionSettings.</summary>
    public int ConnectionSpeed { get; set; } = 30;

    // === Queue (5) ===

    /// <summary>Maximum simultaneously active downloads. Source: QueueSettings.</summary>
    public int MaxActiveDownloads { get; set; } = 5;

    /// <summary>Maximum simultaneously active seeds (-1 = unlimited). Source: QueueSettings.</summary>
    public int MaxActiveSeeds { get; set; } = -1;

    /// <summary>Maximum total active torrents. Source: QueueSettings.</summary>
    public int MaxActiveTorrents { get; set; } = 10;

    /// <summary>Inactive (slow) torrents bypass per-type slot limits. Source: QueueSettings.</summary>
    public bool DontCountSlowTorrents { get; set; } = true;

    /// <summary>Connect to 1 seed for every N download connections. Source: QueueSettings.</summary>
    public int ConnectSeedEveryNDownload { get; set; } = 10;

    // === Choking (6) ===

    /// <summary>Download unchoking strategy. Source: BehaviorSettings.</summary>
    public ChokingAlgorithm ChokingAlgorithm { get; set; } = ChokingAlgorithm.RateBased;

    /// <summary>Seed unchoking strategy. Source: BehaviorSettings.</summary>
    public SeedChokingAlgorithm SeedChokingAlgorithm { get; set; } = SeedChokingAlgorithm.FastestUpload;

    /// <summary>Global cap on unchoked peers. Source: BehaviorSettings.</summary>
    public int UnchokeSlots { get; set; } = 8;

    /// <summary>Interval between unchoke rounds (seconds). Source: PeerSettings.</summary>
    public int UnchokeInterval { get; set; } = 15;

    /// <summary>Interval for optimistic unchoke (seconds). Source: PeerSettings.</summary>
    public int OptimisticUnchokeInterval { get; set; } = 30;

    /// <summary>Number of optimistic unchoke slots (0 = auto). Source: PeerSettings.</summary>
    public int NumOptimisticUnchokeSlots { get; set; } = 0;

    // === Peer (4) ===

    /// <summary>Percentage of peers to disconnect per turnover interval. Source: BehaviorSettings.</summary>
    public int PeerTurnover { get; set; } = 4;

    /// <summary>Only trigger turnover when connected above this percentage. Source: BehaviorSettings.</summary>
    public int PeerTurnoverCutoff { get; set; } = 90;

    /// <summary>Seconds between peer turnover cycles. Source: BehaviorSettings.</summary>
    public int PeerTurnoverInterval { get; set; } = 300;

    /// <summary>Maximum pending block requests per peer. Source: PeerSettings.</summary>
    public int MaxPendingBlocksPerPeer { get; set; } = 500;

    // === Disk (4) ===

    /// <summary>Disk I/O backend selection strategy. Source: DiskSettings.</summary>
    public DiskBackendType BackendType { get; set; } = DiskBackendType.Auto;

    /// <summary>Disk cache size in bytes. Source: DiskSettings.</summary>
    public long CacheSize { get; set; } = 64 * 1024 * 1024;

    /// <summary>Maximum outstanding disk requests. Source: DiskSettings.</summary>
    public int MaxOutstandingDiskRequests { get; set; } = 64;

    /// <summary>Number of hash verification threads. Source: DiskSettings.</summary>
    public int HashThreads { get; set; } = 2;

    // === Seeding (4) ===

    /// <summary>Share ratio limit before stopping seed (0 = unlimited). Source: BehaviorSettings.</summary>
    public float SeedRatioLimit { get; set; } = 0f;

    /// <summary>Seed time limit in minutes (0 = unlimited). Source: BehaviorSettings.</summary>
    public int SeedTimeLimit { get; set; } = 0;

    /// <summary>Pause torrent when seed ratio/time reached. Source: BehaviorSettings.</summary>
    public bool PauseOnSeedComplete { get; set; } = false;

    /// <summary>Remove torrent when seed ratio/time reached. Source: BehaviorSettings.</summary>
    public bool RemoveOnSeedComplete { get; set; } = false;

    // === Picker (2) ===

    /// <summary>Pieces before switching from sequential to rarest-first. Source: BehaviorSettings.</summary>
    public int InitialPickerThreshold { get; set; } = 4;

    /// <summary>Seconds threshold for assigning whole pieces to fast peers. Source: BehaviorSettings.</summary>
    public int WholePiecesThreshold { get; set; } = 20;

    // === Methods ===

    /// <summary>
    /// Capture all 35 profile-relevant values from a GlobalSettings instance.
    /// </summary>
    public static ProfileSettingsValues SnapshotFrom(GlobalSettings gs)
    {
        return new ProfileSettingsValues
        {
            // Bandwidth
            GlobalDownloadLimit = gs.Bandwidth.GlobalDownloadLimit,
            GlobalUploadLimit = gs.Bandwidth.GlobalUploadLimit,
            PerTorrentDownloadLimit = gs.Bandwidth.PerTorrentDownloadLimit,
            PerTorrentUploadLimit = gs.Bandwidth.PerTorrentUploadLimit,
            MixedModeAlgorithm = gs.Bandwidth.MixedModeAlgorithm,

            // Connection
            MaxGlobalConnections = gs.Connection.MaxGlobalConnections,
            MaxConnectionsPerTorrent = gs.Connection.MaxConnectionsPerTorrent,
            MaxUploadsPerTorrent = gs.Connection.MaxUploadsPerTorrent,
            MaxHalfOpenConnections = gs.Connection.MaxHalfOpenConnections,
            ConnectionSpeed = gs.Connection.ConnectionSpeed,

            // Queue
            MaxActiveDownloads = gs.Queue.MaxActiveDownloads,
            MaxActiveSeeds = gs.Queue.MaxActiveSeeds,
            MaxActiveTorrents = gs.Queue.MaxActiveTorrents,
            DontCountSlowTorrents = gs.Queue.DontCountSlowTorrents,
            ConnectSeedEveryNDownload = gs.Queue.ConnectSeedEveryNDownload,

            // Choking
            ChokingAlgorithm = gs.Behavior.ChokingAlgorithm,
            SeedChokingAlgorithm = gs.Behavior.SeedChokingAlgorithm,
            UnchokeSlots = gs.Behavior.UnchokeSlots,
            UnchokeInterval = gs.Peer.UnchokeInterval,
            OptimisticUnchokeInterval = gs.Peer.OptimisticUnchokeInterval,
            NumOptimisticUnchokeSlots = gs.Peer.NumOptimisticUnchokeSlots,

            // Peer
            PeerTurnover = gs.Behavior.PeerTurnover,
            PeerTurnoverCutoff = gs.Behavior.PeerTurnoverCutoff,
            PeerTurnoverInterval = gs.Behavior.PeerTurnoverInterval,
            MaxPendingBlocksPerPeer = gs.Peer.MaxPendingBlocksPerPeer,

            // Disk
            BackendType = gs.Disk.BackendType,
            CacheSize = gs.Disk.CacheSize,
            MaxOutstandingDiskRequests = gs.Disk.MaxOutstandingDiskRequests,
            HashThreads = gs.Disk.HashThreads,

            // Seeding
            SeedRatioLimit = gs.Behavior.SeedRatioLimit,
            SeedTimeLimit = gs.Behavior.SeedTimeLimit,
            PauseOnSeedComplete = gs.Behavior.PauseOnSeedComplete,
            RemoveOnSeedComplete = gs.Behavior.RemoveOnSeedComplete,

            // Picker
            InitialPickerThreshold = gs.Behavior.InitialPickerThreshold,
            WholePiecesThreshold = gs.Behavior.WholePiecesThreshold
        };
    }

    /// <summary>
    /// Write all 35 values back into a GlobalSettings instance.
    /// </summary>
    public void ApplyTo(GlobalSettings gs)
    {
        // Bandwidth
        gs.Bandwidth.GlobalDownloadLimit = GlobalDownloadLimit;
        gs.Bandwidth.GlobalUploadLimit = GlobalUploadLimit;
        gs.Bandwidth.PerTorrentDownloadLimit = PerTorrentDownloadLimit;
        gs.Bandwidth.PerTorrentUploadLimit = PerTorrentUploadLimit;
        gs.Bandwidth.MixedModeAlgorithm = MixedModeAlgorithm;

        // Connection
        gs.Connection.MaxGlobalConnections = MaxGlobalConnections;
        gs.Connection.MaxConnectionsPerTorrent = MaxConnectionsPerTorrent;
        gs.Connection.MaxUploadsPerTorrent = MaxUploadsPerTorrent;
        gs.Connection.MaxHalfOpenConnections = MaxHalfOpenConnections;
        gs.Connection.ConnectionSpeed = ConnectionSpeed;

        // Queue
        gs.Queue.MaxActiveDownloads = MaxActiveDownloads;
        gs.Queue.MaxActiveSeeds = MaxActiveSeeds;
        gs.Queue.MaxActiveTorrents = MaxActiveTorrents;
        gs.Queue.DontCountSlowTorrents = DontCountSlowTorrents;
        gs.Queue.ConnectSeedEveryNDownload = ConnectSeedEveryNDownload;

        // Choking
        gs.Behavior.ChokingAlgorithm = ChokingAlgorithm;
        gs.Behavior.SeedChokingAlgorithm = SeedChokingAlgorithm;
        gs.Behavior.UnchokeSlots = UnchokeSlots;
        gs.Peer.UnchokeInterval = UnchokeInterval;
        gs.Peer.OptimisticUnchokeInterval = OptimisticUnchokeInterval;
        gs.Peer.NumOptimisticUnchokeSlots = NumOptimisticUnchokeSlots;

        // Peer
        gs.Behavior.PeerTurnover = PeerTurnover;
        gs.Behavior.PeerTurnoverCutoff = PeerTurnoverCutoff;
        gs.Behavior.PeerTurnoverInterval = PeerTurnoverInterval;
        gs.Peer.MaxPendingBlocksPerPeer = MaxPendingBlocksPerPeer;

        // Disk
        gs.Disk.BackendType = BackendType;
        gs.Disk.CacheSize = CacheSize;
        gs.Disk.MaxOutstandingDiskRequests = MaxOutstandingDiskRequests;
        gs.Disk.HashThreads = HashThreads;

        // Seeding
        gs.Behavior.SeedRatioLimit = SeedRatioLimit;
        gs.Behavior.SeedTimeLimit = SeedTimeLimit;
        gs.Behavior.PauseOnSeedComplete = PauseOnSeedComplete;
        gs.Behavior.RemoveOnSeedComplete = RemoveOnSeedComplete;

        // Picker
        gs.Behavior.InitialPickerThreshold = InitialPickerThreshold;
        gs.Behavior.WholePiecesThreshold = WholePiecesThreshold;
    }

    /// <summary>
    /// Compare two ProfileSettingsValues for equality, using epsilon for float fields.
    /// </summary>
    public bool ValueEquals(ProfileSettingsValues other)
    {
        if (other is null) return false;

        const float epsilon = 1e-4f;

        return GlobalDownloadLimit == other.GlobalDownloadLimit
            && GlobalUploadLimit == other.GlobalUploadLimit
            && PerTorrentDownloadLimit == other.PerTorrentDownloadLimit
            && PerTorrentUploadLimit == other.PerTorrentUploadLimit
            && MixedModeAlgorithm == other.MixedModeAlgorithm
            && MaxGlobalConnections == other.MaxGlobalConnections
            && MaxConnectionsPerTorrent == other.MaxConnectionsPerTorrent
            && MaxUploadsPerTorrent == other.MaxUploadsPerTorrent
            && MaxHalfOpenConnections == other.MaxHalfOpenConnections
            && ConnectionSpeed == other.ConnectionSpeed
            && MaxActiveDownloads == other.MaxActiveDownloads
            && MaxActiveSeeds == other.MaxActiveSeeds
            && MaxActiveTorrents == other.MaxActiveTorrents
            && DontCountSlowTorrents == other.DontCountSlowTorrents
            && ConnectSeedEveryNDownload == other.ConnectSeedEveryNDownload
            && ChokingAlgorithm == other.ChokingAlgorithm
            && SeedChokingAlgorithm == other.SeedChokingAlgorithm
            && UnchokeSlots == other.UnchokeSlots
            && UnchokeInterval == other.UnchokeInterval
            && OptimisticUnchokeInterval == other.OptimisticUnchokeInterval
            && NumOptimisticUnchokeSlots == other.NumOptimisticUnchokeSlots
            && PeerTurnover == other.PeerTurnover
            && PeerTurnoverCutoff == other.PeerTurnoverCutoff
            && PeerTurnoverInterval == other.PeerTurnoverInterval
            && MaxPendingBlocksPerPeer == other.MaxPendingBlocksPerPeer
            && BackendType == other.BackendType
            && CacheSize == other.CacheSize
            && MaxOutstandingDiskRequests == other.MaxOutstandingDiskRequests
            && HashThreads == other.HashThreads
            && MathF.Abs(SeedRatioLimit - other.SeedRatioLimit) < epsilon
            && SeedTimeLimit == other.SeedTimeLimit
            && PauseOnSeedComplete == other.PauseOnSeedComplete
            && RemoveOnSeedComplete == other.RemoveOnSeedComplete
            && InitialPickerThreshold == other.InitialPickerThreshold
            && WholePiecesThreshold == other.WholePiecesThreshold;
    }
}

/// <summary>
/// Export/import format for .vtprofile.json files.
/// </summary>
public class ProfileExportData
{
    [JsonPropertyName("profileFormatVersion")]
    public int ProfileFormatVersion { get; set; } = 1;

    [JsonPropertyName("appVersion")]
    public int AppVersion { get; set; } = GlobalSettings.CurrentVersion;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#2196F3";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "performance";

    [JsonPropertyName("checksum")]
    public string Checksum { get; set; } = "";

    [JsonPropertyName("settings")]
    public ProfileSettingsValues Settings { get; set; } = new();
}
