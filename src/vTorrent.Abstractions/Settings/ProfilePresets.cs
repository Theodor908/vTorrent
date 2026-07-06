using System.Collections.Generic;
using System.Linq;
using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Built-in profile presets: Quiet, Balanced, Performance.
/// </summary>
public static class ProfilePresets
{
    /// <summary>
    /// Low-resource profile: minimal connections, conservative limits, background-friendly.
    /// </summary>
    public static readonly ProfileSettings Quiet = new()
    {
        Name = "Quiet",
        Color = "#78909C",
        Scope = "performance",
        Settings = new ProfileSettingsValues
        {
            // Bandwidth
            GlobalDownloadLimit = 1 * 1024 * 1024,   // 1 MB/s
            GlobalUploadLimit = 256 * 1024,            // 256 KB/s
            PerTorrentDownloadLimit = 0,
            PerTorrentUploadLimit = 0,
            MixedModeAlgorithm = MixedModeAlgorithm.PreferUtp,

            // Connection
            MaxGlobalConnections = 100,
            MaxConnectionsPerTorrent = 50,
            MaxUploadsPerTorrent = 2,
            MaxHalfOpenConnections = 10,
            ConnectionSpeed = 5,                       // libtorrent min_memory_usage exact value

            // Queue
            MaxActiveDownloads = 2,
            MaxActiveSeeds = 3,
            MaxActiveTorrents = 4,
            DontCountSlowTorrents = true,
            ConnectSeedEveryNDownload = 10,

            // Choking
            ChokingAlgorithm = ChokingAlgorithm.RateBased,
            SeedChokingAlgorithm = SeedChokingAlgorithm.FastestUpload,
            UnchokeSlots = 4,
            UnchokeInterval = 15,
            OptimisticUnchokeInterval = 30,
            NumOptimisticUnchokeSlots = 0,

            // Peer
            PeerTurnover = 2,
            PeerTurnoverCutoff = 90,
            PeerTurnoverInterval = 300,
            MaxPendingBlocksPerPeer = 100,             // Reduce memory for outstanding requests

            // Disk
            BackendType = DiskBackendType.Auto,
            CacheSize = 16 * 1024 * 1024,              // 16 MB — small cache footprint
            MaxOutstandingDiskRequests = 16,           // Reduce disk I/O pressure
            HashThreads = 1,                           // libtorrent min_memory_usage exact value

            // Seeding
            SeedRatioLimit = 1.0f,
            SeedTimeLimit = 1440,                      // 24 hours
            PauseOnSeedComplete = true,
            RemoveOnSeedComplete = false,

            // Picker
            InitialPickerThreshold = 4,
            WholePiecesThreshold = 2                   // libtorrent min_memory_usage exact value
        }
    };

    /// <summary>
    /// Default/balanced profile: matches all GlobalSettings defaults.
    /// </summary>
    public static readonly ProfileSettings Balanced = new()
    {
        Name = "Balanced",
        Color = "#2196F3",
        Scope = "performance",
        Settings = new ProfileSettingsValues()
    };

    /// <summary>
    /// High-performance profile: aggressive connections, large cache, maximum throughput.
    /// </summary>
    public static readonly ProfileSettings Performance = new()
    {
        Name = "Performance",
        Color = "#F44336",
        Scope = "performance",
        Settings = new ProfileSettingsValues
        {
            // Bandwidth — unlimited (defaults)
            GlobalDownloadLimit = 0,
            GlobalUploadLimit = 0,
            PerTorrentDownloadLimit = 0,
            PerTorrentUploadLimit = 0,
            MixedModeAlgorithm = MixedModeAlgorithm.PreferTcp,

            // Connection
            MaxGlobalConnections = 2000,
            MaxConnectionsPerTorrent = 500,
            MaxUploadsPerTorrent = -1,                 // unlimited — no artificial upload cap
            MaxHalfOpenConnections = 200,
            ConnectionSpeed = 200,                     // libtorrent uses 500; 200 is safer for desktop

            // Queue
            MaxActiveDownloads = 20,
            MaxActiveSeeds = -1,                       // unlimited
            MaxActiveTorrents = -1,                    // unlimited
            DontCountSlowTorrents = true,
            ConnectSeedEveryNDownload = 10,

            // Choking
            ChokingAlgorithm = ChokingAlgorithm.FixedSlots,  // libtorrent high_performance_seed exact value
            SeedChokingAlgorithm = SeedChokingAlgorithm.FastestUpload,
            UnchokeSlots = -1,                         // unlimited — libtorrent high_performance_seed exact value
            UnchokeInterval = 15,
            OptimisticUnchokeInterval = 30,
            NumOptimisticUnchokeSlots = 0,

            // Peer
            PeerTurnover = 8,
            PeerTurnoverCutoff = 90,
            PeerTurnoverInterval = 300,
            MaxPendingBlocksPerPeer = 1500,            // libtorrent high_performance_seed exact value

            // Disk
            BackendType = DiskBackendType.Auto,
            CacheSize = 512 * 1024 * 1024,            // 512 MB — large cache for throughput
            MaxOutstandingDiskRequests = 256,          // Keep disk subsystem busy
            HashThreads = 4,

            // Seeding — no limits (defaults)
            SeedRatioLimit = 0f,
            SeedTimeLimit = 0,
            PauseOnSeedComplete = false,
            RemoveOnSeedComplete = false,

            // Picker
            InitialPickerThreshold = 4,
            WholePiecesThreshold = 20
        }
    };

    /// <summary>
    /// All built-in presets.
    /// </summary>
    public static readonly IReadOnlyList<ProfileSettings> All = new[]
    {
        Quiet,
        Balanced,
        Performance
    };

    /// <summary>
    /// Get a built-in profile by name (case-insensitive), or null if not found.
    /// </summary>
    public static ProfileSettings? GetBuiltIn(string name)
    {
        return All.FirstOrDefault(p => string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Check if a name matches a built-in profile (case-insensitive).
    /// </summary>
    public static bool IsBuiltIn(string name)
    {
        return GetBuiltIn(name) is not null;
    }
}
