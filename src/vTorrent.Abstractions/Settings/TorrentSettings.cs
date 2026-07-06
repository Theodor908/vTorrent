using System;
using System.Collections.Generic;
using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Per-torrent settings that override global defaults.
/// Values of -1 or null mean "use global setting".
/// </summary>
public class TorrentSettings
{
    /// <summary>
    /// Info hash of the torrent this applies to
    /// </summary>
    public string InfoHash { get; set; } = string.Empty;

    /// <summary>
    /// When settings were last modified
    /// </summary>
    public DateTime UpdatedOn { get; set; } = DateTime.UtcNow;

    #region Connection Overrides

    /// <summary>
    /// Maximum connections for this torrent (-1 = use global)
    /// </summary>
    public int MaxConnections { get; set; } = -1;

    /// <summary>
    /// Maximum upload slots for this torrent (-1 = use global)
    /// </summary>
    public int MaxUploads { get; set; } = -1;

    #endregion

    #region Bandwidth Overrides

    /// <summary>
    /// Upload rate limit in bytes/s (-1 = use global, 0 = unlimited)
    /// </summary>
    public int UploadLimit { get; set; } = -1;

    /// <summary>
    /// Download rate limit in bytes/s (-1 = use global, 0 = unlimited)
    /// </summary>
    public int DownloadLimit { get; set; } = -1;

    #endregion

    #region Download Options

    /// <summary>
    /// Enable sequential download mode
    /// </summary>
    public bool SequentialDownload { get; set; } = false;

    /// <summary>
    /// Prioritize downloading the first and last pieces of each file first.
    /// Useful for media preview / streaming scenarios.
    /// </summary>
    public bool FirstLastPiecePriority { get; set; } = false;

    /// <summary>
    /// Allow torrent to be auto-managed (queued)
    /// </summary>
    public bool AutoManaged { get; set; } = true;

    /// <summary>
    /// Download priority (High, Normal, Low)
    /// </summary>
    public TorrentPriority Priority { get; set; } = TorrentPriority.Normal;

    #endregion

    #region Seeding Options

    /// <summary>
    /// Enable super-seeding mode (BEP 16). Only effective when torrent is fully seeded.
    /// </summary>
    public bool SuperSeeding { get; set; } = false;

    /// <summary>
    /// Seeding limits for this torrent
    /// </summary>
    public SeedingLimits Seeding { get; set; } = new();

    #endregion

    #region Additional Configuration

    /// <summary>
    /// Additional tracker URLs to use
    /// </summary>
    public List<string>? CustomTrackers { get; set; }

    /// <summary>
    /// User-defined category
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// User-defined tags
    /// </summary>
    public List<string>? Tags { get; set; }

    /// <summary>
    /// Custom save path override (null = use global default)
    /// </summary>
    public string? SavePath { get; set; }

    /// <summary>
    /// User-defined display name override (null = use torrent's info dict name)
    /// </summary>
    public string? DisplayName { get; set; }

    #endregion

    #region Medium-Effort Setting Overrides

    /// <summary>Per-torrent choking algorithm override. null = use global</summary>
    public ChokingAlgorithm? ChokingAlgorithm { get; set; }

    /// <summary>Per-torrent seed choking algorithm override. null = use global</summary>
    public SeedChokingAlgorithm? SeedChokingAlgorithm { get; set; }

    /// <summary>Per-torrent optimistic unchoke slots. -1 = use global (0 at global = auto)</summary>
    public int NumOptimisticUnchokeSlots { get; set; } = -1;

    /// <summary>Per-torrent mixed mode algorithm override. null = use global</summary>
    public MixedModeAlgorithm? MixedModeAlgorithm { get; set; }

    /// <summary>Per-torrent peer turnover percentage. -1 = use global</summary>
    public int PeerTurnover { get; set; } = -1;

    /// <summary>Per-torrent peer turnover cutoff percentage. -1 = use global</summary>
    public int PeerTurnoverCutoff { get; set; } = -1;

    /// <summary>Per-torrent peer turnover interval in seconds. -1 = use global</summary>
    public int PeerTurnoverInterval { get; set; } = -1;

    /// <summary>Per-torrent piece extent affinity override. null = use global</summary>
    public bool? PieceExtentAffinity { get; set; }

    /// <summary>Per-torrent piece extent size in bytes. -1 = use global</summary>
    public int PieceExtentSize { get; set; } = -1;

    /// <summary>Per-torrent disk backend override. null = use global</summary>
    public DiskBackendType? DiskBackend { get; set; }

    /// <summary>Per-torrent disk write mode override. null = use global</summary>
    public DiskIoMode? DiskWriteMode { get; set; }

    #endregion

    #region Methods

    /// <summary>
    /// Merge with global settings, returning effective values
    /// </summary>
    public EffectiveTorrentSettings MergeWith(GlobalSettings global)
    {
        return new EffectiveTorrentSettings
        {
            InfoHash = InfoHash,

            // Connection
            MaxConnections = MaxConnections >= 0 ? MaxConnections : global.Connection.MaxConnectionsPerTorrent,
            MaxUploads = MaxUploads >= 0 ? MaxUploads : global.Connection.MaxUploadsPerTorrent,

            // Bandwidth
            UploadLimit = UploadLimit >= 0 ? UploadLimit : global.Bandwidth.PerTorrentUploadLimit,
            DownloadLimit = DownloadLimit >= 0 ? DownloadLimit : global.Bandwidth.PerTorrentDownloadLimit,

            // Download options
            SequentialDownload = SequentialDownload,
            FirstLastPiecePriority = FirstLastPiecePriority,
            AutoManaged = AutoManaged,
            Priority = Priority,

            // Seeding
            SeedRatioLimit = Seeding.RatioLimit ?? global.Behavior.SeedRatioLimit,
            SeedTimeLimit = Seeding.TimeLimitMinutes ?? global.Behavior.SeedTimeLimit,
            StopWhenSeedComplete = Seeding.StopWhenComplete ?? global.Behavior.RemoveOnSeedComplete,
            PauseWhenSeedComplete = Seeding.PauseWhenComplete ?? global.Behavior.PauseOnSeedComplete,

            // Additional
            CustomTrackers = CustomTrackers ?? new List<string>(),
            Category = Category,
            Tags = Tags ?? new List<string>(),
            SavePath = SavePath ?? global.Disk.DefaultSavePath,

            // Choking (medium-effort overrides)
            ChokingAlgorithm = ChokingAlgorithm ?? global.Behavior.ChokingAlgorithm,
            SeedChokingAlgorithm = SeedChokingAlgorithm ?? global.Behavior.SeedChokingAlgorithm,
            NumOptimisticUnchokeSlots = NumOptimisticUnchokeSlots >= 0 ? NumOptimisticUnchokeSlots : global.Peer.NumOptimisticUnchokeSlots,
            MixedModeAlgorithm = MixedModeAlgorithm ?? global.Bandwidth.MixedModeAlgorithm,

            // Peer turnover
            PeerTurnover = PeerTurnover >= 0 ? PeerTurnover : global.Behavior.PeerTurnover,
            PeerTurnoverCutoff = PeerTurnoverCutoff >= 0 ? PeerTurnoverCutoff : global.Behavior.PeerTurnoverCutoff,
            PeerTurnoverInterval = PeerTurnoverInterval >= 0 ? PeerTurnoverInterval : global.Behavior.PeerTurnoverInterval,

            // Disk
            PieceExtentAffinity = PieceExtentAffinity ?? global.Disk.PieceExtentAffinity,
            PieceExtentSize = PieceExtentSize >= 0 ? PieceExtentSize : global.Disk.PieceExtentSize,
            DiskBackend = DiskBackend ?? global.Disk.BackendType,
            DiskWriteMode = DiskWriteMode ?? global.Disk.WriteMode,
        };
    }

    /// <summary>
    /// Create a copy of these settings
    /// </summary>
    public TorrentSettings Clone()
    {
        return new TorrentSettings
        {
            InfoHash = InfoHash,
            UpdatedOn = UpdatedOn,
            MaxConnections = MaxConnections,
            MaxUploads = MaxUploads,
            UploadLimit = UploadLimit,
            DownloadLimit = DownloadLimit,
            SequentialDownload = SequentialDownload,
            FirstLastPiecePriority = FirstLastPiecePriority,
            AutoManaged = AutoManaged,
            Priority = Priority,
            SuperSeeding = SuperSeeding,
            Seeding = new SeedingLimits
            {
                RatioLimit = Seeding.RatioLimit,
                TimeLimitMinutes = Seeding.TimeLimitMinutes,
                StopWhenComplete = Seeding.StopWhenComplete,
                PauseWhenComplete = Seeding.PauseWhenComplete
            },
            CustomTrackers = CustomTrackers != null ? new List<string>(CustomTrackers) : null,
            Category = Category,
            Tags = Tags != null ? new List<string>(Tags) : null,
            SavePath = SavePath,
            DisplayName = DisplayName,
            ChokingAlgorithm = ChokingAlgorithm,
            SeedChokingAlgorithm = SeedChokingAlgorithm,
            NumOptimisticUnchokeSlots = NumOptimisticUnchokeSlots,
            MixedModeAlgorithm = MixedModeAlgorithm,
            PeerTurnover = PeerTurnover,
            PeerTurnoverCutoff = PeerTurnoverCutoff,
            PeerTurnoverInterval = PeerTurnoverInterval,
            PieceExtentAffinity = PieceExtentAffinity,
            PieceExtentSize = PieceExtentSize,
            DiskBackend = DiskBackend,
            DiskWriteMode = DiskWriteMode
        };
    }

    #endregion
}

/// <summary>
/// Seeding limits for a torrent
/// </summary>
public class SeedingLimits
{
    /// <summary>
    /// Share ratio limit (null = use global)
    /// </summary>
    public float? RatioLimit { get; set; }

    /// <summary>
    /// Seed time limit in minutes (null = use global)
    /// </summary>
    public int? TimeLimitMinutes { get; set; }

    /// <summary>
    /// Stop (remove) torrent when limits reached
    /// </summary>
    public bool? StopWhenComplete { get; set; }

    /// <summary>
    /// Pause torrent when limits reached
    /// </summary>
    public bool? PauseWhenComplete { get; set; }
}

/// <summary>
/// Torrent download priority
/// </summary>
public enum TorrentPriority
{
    Low = 1,
    Normal = 4,
    High = 7
}

/// <summary>
/// Effective torrent settings after merging per-torrent with global
/// </summary>
public class EffectiveTorrentSettings
{
    public string InfoHash { get; set; } = string.Empty;

    // Connection
    public int MaxConnections { get; set; }
    public int MaxUploads { get; set; }

    // Bandwidth
    public int UploadLimit { get; set; }
    public int DownloadLimit { get; set; }

    // Download
    public bool SequentialDownload { get; set; }
    public bool FirstLastPiecePriority { get; set; }
    public bool AutoManaged { get; set; }
    public TorrentPriority Priority { get; set; }

    // Seeding
    public float SeedRatioLimit { get; set; }
    public int SeedTimeLimit { get; set; }
    public bool StopWhenSeedComplete { get; set; }
    public bool PauseWhenSeedComplete { get; set; }

    // Additional
    public List<string> CustomTrackers { get; set; } = new();
    public string? Category { get; set; }
    public List<string> Tags { get; set; } = new();
    public string SavePath { get; set; } = string.Empty;

    // Choking (medium-effort)
    public ChokingAlgorithm ChokingAlgorithm { get; set; } = ChokingAlgorithm.RateBased;
    public SeedChokingAlgorithm SeedChokingAlgorithm { get; set; } = SeedChokingAlgorithm.FastestUpload;
    public int NumOptimisticUnchokeSlots { get; set; }
    public MixedModeAlgorithm MixedModeAlgorithm { get; set; } = MixedModeAlgorithm.PeerProportional;

    // Peer turnover
    public int PeerTurnover { get; set; }
    public int PeerTurnoverCutoff { get; set; }
    public int PeerTurnoverInterval { get; set; }

    // Disk
    public bool PieceExtentAffinity { get; set; }
    public int PieceExtentSize { get; set; }
    public DiskBackendType DiskBackend { get; set; }
    public DiskIoMode DiskWriteMode { get; set; }
}
