using System;

using System.Collections.Generic;

using System.Linq;

using System.Security.Cryptography;

using Microsoft.Extensions.Logging;

using vTorrent.Bencode.Objects;

using vTorrent.Bencode.Parsers;

using vTorrent.Bencode.Torrents;

using vTorrent.Core.ResumeData;

using vTorrent.Core.Session;

using vTorrent.Core.Merkle;

using vTorrent.Core.State;

using vTorrent.Storage;

using vTorrent.Abstractions.Enums;

using vTorrent.Abstractions.Models;

using vTorrent.Abstractions.Records;
using vTorrent.Core.Engine;
using vTorrent.Core.TorrentSigning;

namespace vTorrent.Core.Orchestration;

/// <summary>

/// Runtime wrapper for a torrent being managed by the orchestrator.

/// Contains the torrent metadata, engine, statistics, and orchestration state.

/// </summary>

public class ManagedTorrent

{

    #region Identity

    /// <summary>

    /// Info hash (hex string)

    /// </summary>

    public string InfoHash { get; }

    /// <summary>

    /// Torrent name

    /// </summary>

    public string Name { get; }

    /// <summary>

    /// Obfuscated hash for encrypted handshakes (if computed)

    /// </summary>

    public string? ObfuscatedHash { get; set; }

    /// <summary>Precomputed SHA1('req2' + infoHash) for MSE inbound identification.</summary>

    public string? Req2Hash { get; set; }

    #endregion

    #region Torrent Data

    /// <summary>

    /// Parsed torrent metadata (may be null for magnet links)

    /// </summary>

    public Torrent? Torrent { get; set; }

    /// <summary>

    /// Resume data with piece state

    /// </summary>

    public TorrentResumeData ResumeData { get; set; }

    /// <summary>

    /// Runtime statistics

    /// </summary>

    public TorrentStatistics Statistics { get; set; }

    /// <summary>

    /// Per-file merkle trees for v2/hybrid torrents. Null for v1-only.

    /// Keyed by file's pieces root (SHA256Hash).

    /// </summary>

    public Dictionary<SHA256Hash, MerkleTree>? MerkleTrees { get; set; }

    /// <summary>
    /// BEP 35: Signature verification results for this torrent (if any).
    /// </summary>
    public List<TorrentSignature>? Signatures { get; set; }

    #endregion

    #region Engine

    /// <summary>

    /// The actual torrent engine (null when not active)

    /// </summary>

    public TorrentEngine? Engine { get; set; }

    /// <summary>

    /// Whether engine is currently running

    /// </summary>

    public bool IsEngineRunning => Engine != null;

    #endregion

    #region State

    // New orthogonal state controller (channel-based, lock-free reads)
    internal TorrentStateController StateController { get; private set; } = null!;

    private int _isDirty;

    public event EventHandler<StatusChangedEventArgs>? StatusChanged
    {
        add => StateController.StatusChanged += value;
        remove => StateController.StatusChanged -= value;
    }

    public TorrentStatus GetStatus() => StateController.GetStatus();

    internal void UpdateStatus(TorrentStatus newStatus, bool force = false)
    {
        // Transitional shim — forwards to controller via PostRestore.
        // Will be removed when all callers are migrated to post triggers directly.
        StateController.PostRestore(
            newStatus.Phase, newStatus.Intent, newStatus.FileOp,
            newStatus.Error, newStatus.MissingFiles, newStatus.IsAutoManaged);
        var current = GetStatus();
        if (newStatus.FileOpProgress != current.FileOpProgress ||
            newStatus.IsFinished != current.IsFinished ||
            newStatus.IsSeed != current.IsSeed)
        {
            StateController.PostMetrics(
                newStatus.FileOpProgress, newStatus.IsFinished, newStatus.IsSeed);
        }
    }

    /// <summary>
    /// Updates only the FileOpProgress field.
    /// Hot path during recheck.
    /// </summary>
    internal void UpdateFileOpProgress(double progress)
    {
        StateController.PostMetrics(fileOpProgress: progress);
    }

    internal void ForceStatus(TorrentStatus newStatus, string reason, Microsoft.Extensions.Logging.ILogger? logger = null)

    {

        logger?.LogWarning("ForceStatus: {Reason} — bypassing transition validation", reason);

        UpdateStatus(newStatus, force: true);

    }

    internal bool TryConsumeDirtyFlag()

    {

        return System.Threading.Interlocked.Exchange(ref _isDirty, 0) == 1;

    }

    /// <summary>

    /// Error message — convenience accessor for the current status Error.Message.

    /// </summary>

    public string? ErrorMessage => GetStatus().Error?.Message;

    /// <summary>

    /// Whether torrent is finished (all pieces downloaded)

    /// </summary>

    public bool IsFinished { get; set; }

    /// <summary>

    /// Whether torrent is a seed (finished and verified)

    /// </summary>

    public bool IsSeed => IsFinished && Statistics.PiecesCompleted == Statistics.TotalPieces;

    #endregion

    #region Queue & Auto-Management

    /// <summary>

    /// Position in download/seed queue

    /// </summary>

    public int QueuePosition { get; set; } = -1;

    /// <summary>

    /// Whether auto-management is enabled for this torrent.

    /// Facade over TorrentStatus — single source of truth.

    /// </summary>

    public bool IsAutoManaged

    {

        get => GetStatus().IsAutoManaged;

        set

        {

            if (GetStatus().IsAutoManaged == value) return;

            StateController.PostAutoManaged(value);

        }

    }

    /// <summary>

    /// Whether user explicitly paused this torrent

    /// (auto-manager won't resume user-paused torrents)

    /// </summary>

    public bool UserPaused { get; set; }

    /// <summary>
    /// Whether this torrent was stopped by the VPN kill switch.
    /// Set to true when VPN goes down, cleared when VPN recovers and torrent is resumed.
    /// Auto-manager skips torrents with this flag set.
    /// </summary>
    public bool IsVpnBlocked { get; set; }

    #endregion

    #region Timing

    /// <summary>

    /// When torrent was added

    /// </summary>

    public DateTime AddedTime { get; set; }

    /// <summary>

    /// When torrent was last active

    /// </summary>

    public DateTime? LastActiveTime { get; set; }

    /// <summary>

    /// When torrent finished downloading

    /// </summary>

    public DateTime? CompletedTime { get; set; }

    #endregion

    #region I2P

    /// <summary>Whether this torrent uses I2P (any tracker URL has .i2p domain).</summary>
    public bool IsI2p => ForceI2p || (Torrent?.AnnounceList?.Any(tier =>
        tier.Any(url => url.EndsWith(".i2p", StringComparison.OrdinalIgnoreCase) ||
                        url.Contains(".i2p/"))) == true) ||
        (Torrent?.Announce?.EndsWith(".i2p", StringComparison.OrdinalIgnoreCase) == true) ||
        (Torrent?.Announce?.Contains(".i2p/") == true);

    /// <summary>Explicitly flag this torrent as I2P (e.g., for magnet links).</summary>
    public bool ForceI2p { get; set; }

    #endregion

    #region Flags

    /// <summary>Whether this torrent has the BEP 27 private flag set (disables DHT/PEX/LPD).</summary>
    public bool IsPrivate => Torrent?.Info?.IsPrivate ?? false;

    /// <summary>

    /// Whether this torrent is in the process of stopping

    /// </summary>

    public bool IsStopping { get; set; }

    /// <summary>

    /// Whether this torrent wants more peer connections

    /// </summary>

    public bool WantsPeers { get; set; } = true;

    /// <summary>

    /// Whether sequential download is enabled

    /// </summary>

    public bool SequentialDownload { get; set; }

    /// <summary>

    /// Whether first and last pieces of each file should be prioritized

    /// </summary>

    public bool FirstLastPiecePriority { get; set; }

    /// <summary>

    /// File priorities to apply when the engine initializes (before download loop starts).

    /// Consumed and cleared by EnginePhaseInitializer during Phase 5.

    /// Follows libtorrent's model: priorities must be set before the piece picker runs.

    /// </summary>

    public FilePriority[]? PendingFilePriorities { get; set; }

    #endregion

    #region Storage

    /// <summary>

    /// Save path for downloaded files

    /// </summary>

    public string SavePath { get; set; } = string.Empty;

    /// <summary>

    /// Path to .torrent file (for seeding)

    /// </summary>

    public string? TorrentFilePath { get; set; }

    #endregion

    #region Category & Tags

    /// <summary>

    /// Category ID (null if uncategorized)

    /// </summary>

    public int? CategoryId { get; set; }

    /// <summary>

    /// Category name (cached for display)

    /// </summary>

    public string? CategoryName { get; set; }

    /// <summary>

    /// Tags associated with this torrent

    /// </summary>

    public List<Tag> Tags { get; set; } = new();

    #endregion

    #region Magnet Link Support

    /// <summary>

    /// Whether this torrent was added via magnet link

    /// </summary>

    public bool IsMagnetLink { get; set; }

    /// <summary>

    /// The original magnet link (if applicable)

    /// </summary>

    public MagnetLink? MagnetLinkData { get; set; }

    /// <summary>

    /// Metadata download progress (0.0 to 1.0)

    /// </summary>

    public double MetadataProgress { get; set; }

    /// <summary>

    /// Number of metadata pieces received

    /// </summary>

    public int MetadataPiecesReceived { get; set; }

    /// <summary>

    /// Total number of metadata pieces

    /// </summary>

    public int MetadataPiecesTotal { get; set; }

    /// <summary>

    /// Whether metadata has been received and validated

    /// </summary>

    public bool HasMetadata => Torrent != null;

    /// <summary>

    /// When metadata was received (for magnet links)

    /// </summary>

    public DateTime? MetadataReceivedTime { get; set; }

    /// <summary>

    /// Info hash bytes (20 bytes for validation)

    /// </summary>

    public byte[]? InfoHashBytes { get; set; }

    /// <summary>

    /// Event raised when metadata is received

    /// </summary>

    public event Action<ManagedTorrent>? MetadataReceived;

    /// <summary>

    /// Sets the metadata for a magnet link torrent after downloading it from peers.

    /// Validates the metadata against the expected info hash.

    /// </summary>

    /// <param name="metadataBytes">The raw bencoded info dictionary.</param>

    /// <returns>True if metadata was valid and set successfully.</returns>

    public bool SetMetadata(byte[] metadataBytes)

    {

        if (metadataBytes == null || metadataBytes.Length == 0)

            return false;

        if (!IsMagnetLink)

            return false;

        // Validate hash

        var hash = SHA1.HashData(metadataBytes);

        var expectedHash = InfoHashBytes ?? Convert.FromHexString(InfoHash);

        if (!hash.AsSpan().SequenceEqual(expectedHash))

            return false;

        try

        {

            // Parse the info dictionary

            var parser = new BencodeParser();

            var infoDict = parser.Parse(metadataBytes, out _) as BDictionary;

            if (infoDict == null)

                return false;

            // Create a minimal torrent dictionary with the info

            var torrentDict = new BDictionary();

            torrentDict.Add("info", infoDict);

            // Add trackers from magnet link

            if (MagnetLinkData?.Trackers?.Count > 0)

            {

                torrentDict.AddString("announce", MagnetLinkData.Trackers[0]);

                if (MagnetLinkData.Trackers.Count > 1)

                {

                    var announceList = new BList();

                    foreach (var tracker in MagnetLinkData.Trackers)

                    {

                        var tier = new BList();

                        tier.Add(new BString(tracker));

                        announceList.Add(tier);

                    }

                    torrentDict.Add("announce-list", announceList);

                }

            }

            // Parse as torrent

            Torrent = TorrentParser.FromBDictionary(torrentDict);

            // Cache the info hash

            Torrent._cachedInfoHash = hash;

            // Update statistics with actual torrent info

            Statistics.TotalSize = Torrent.TotalSize;

            Statistics.TotalWanted = Torrent.TotalSize;

            Statistics.TotalPieces = Torrent.PieceCount;

            // Update resume data

            ResumeData.PieceCount = Torrent.PieceCount;

            ResumeData.PieceLength = (int)Torrent.Info.PieceLength;

            // Initialize empty bitfield for downloaded pieces (all zeros = nothing downloaded yet)

            ResumeData.HavePieces = new byte[(Torrent.PieceCount + 7) / 8];

            MetadataReceivedTime = DateTime.UtcNow;

            // Notify listeners

            MetadataReceived?.Invoke(this);

            return true;

        }

        catch

        {

            return false;

        }

    }

    #endregion

    #region Constructor

    public ManagedTorrent(string infoHash, string name, ILogger? logger = null)

    {

        InfoHash = infoHash ?? throw new ArgumentNullException(nameof(infoHash));

        Name = name ?? throw new ArgumentNullException(nameof(name));

        ResumeData = new TorrentResumeData { InfoHash = infoHash, Name = name };

        Statistics = new TorrentStatistics();

        AddedTime = DateTime.UtcNow;

        StateController = new TorrentStateController(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        StateController.StatusChanged += (_, _) => System.Threading.Interlocked.Exchange(ref _isDirty, 1);

    }

    #endregion

    #region State Management

    /// <summary>

    /// Mark torrent as errored

    /// </summary>

    public void SetError(string message)

    {

        StateController.PostError(new TorrentError { Message = message });

        Statistics.Error = new TorrentError { Message = message };

    }

    /// <summary>

    /// Clear error state

    /// </summary>

    public void ClearError()

    {

        if (GetStatus().Error.HasValue)

        {

            StateController.PostClearError();

            Statistics.Error = null;

            Statistics.Intent = UserIntent.Paused;

        }

    }

    #endregion

    #region Progress

    /// <summary>

    /// Download progress (0.0 to 1.0)

    /// </summary>

    public double Progress

    {

        get

        {

            if (Statistics.TotalSize == 0) return 0;

            return Statistics.Progress;

        }

    }

    /// <summary>

    /// Total size in bytes

    /// </summary>

    public long TotalSize => Statistics.TotalSize;

    /// <summary>

    /// Downloaded bytes

    /// </summary>

    public long Downloaded => Statistics.TotalDone;

    /// <summary>

    /// Uploaded bytes (all time)

    /// </summary>

    public long Uploaded => Statistics.AllTimeUploaded;

    /// <summary>

    /// Share ratio (uploaded / downloaded)

    /// </summary>

    public double Ratio

    {

        get

        {

            if (Statistics.AllTimeDownloaded == 0) return 0;

            return (double)Statistics.AllTimeUploaded / Statistics.AllTimeDownloaded;

        }

    }

    #endregion

    #region Transfer Rates

    /// <summary>

    /// Current download rate (bytes/sec)

    /// </summary>

    public int DownloadRate => (int)Statistics.DownloadRate;

    /// <summary>

    /// Current upload rate (bytes/sec)

    /// </summary>

    public int UploadRate => (int)Statistics.UploadRate;

    #endregion

    #region Peers

    /// <summary>

    /// Number of connected peers

    /// </summary>

    public int ConnectedPeers => Statistics.ConnectedPeers;

    /// <summary>

    /// Number of connected seeds

    /// </summary>

    public int ConnectedSeeds => Statistics.ConnectedSeeds;

    #endregion

    #region Factory

    /// <summary>

    /// Create from database record and resume data

    /// </summary>

    public static ManagedTorrent FromRecord(

        TorrentRecord record,

        TorrentResumeData? resumeData)

    {

        var managed = new ManagedTorrent(record.InfoHash, record.Name)

        {

            SavePath = record.SavePath,

            TorrentFilePath = record.TorrentFilePath,

            QueuePosition = record.QueuePosition,

            IsAutoManaged = record.AutoManaged,

            SequentialDownload = record.SequentialDownload,

            FirstLastPiecePriority = record.FirstLastPiecePriority,

            IsFinished = record.IsFinished,

            CategoryId = record.CategoryId,

            AddedTime = DateTimeOffset.FromUnixTimeSeconds(record.AddedAt).DateTime,

            CompletedTime = record.CompletedAt.HasValue

                ? DateTimeOffset.FromUnixTimeSeconds(record.CompletedAt.Value).DateTime

                : null,

            // CRITICAL: Restore LastActiveTime for fast resume to work

            // Without this, CheckFilesModifiedAsync will use AddedTime which is too old

            LastActiveTime = record.LastActiveAt.HasValue

                ? DateTimeOffset.FromUnixTimeSeconds(record.LastActiveAt.Value).DateTime

                : (resumeData?.LastSaved > 0

                    ? DateTimeOffset.FromUnixTimeSeconds(resumeData.LastSaved).DateTime

                    : null),

            ResumeData = resumeData ?? new TorrentResumeData

            {

                InfoHash = record.InfoHash,

                Name = record.Name,

                PieceCount = record.PieceCount,

                PieceLength = record.PieceSize,

                SavePath = record.SavePath

            },

            Statistics = new TorrentStatistics

            {

                Phase = ParsePhase(record.TransferPhase),

                Intent = ParseIntent(record.UserIntent),

                AddedTime = DateTimeOffset.FromUnixTimeSeconds(record.AddedAt).DateTime,

                CompletedTime = record.CompletedAt.HasValue

                    ? DateTimeOffset.FromUnixTimeSeconds(record.CompletedAt.Value).DateTime

                    : null,

                TotalSize = record.TotalSize,

                TotalWanted = record.TotalSize,

                TotalPieces = record.PieceCount,

                AllTimeUploaded = record.TotalUploaded,

                AllTimeDownloaded = record.TotalDownloaded,

                ActiveDuration = TimeSpan.FromSeconds(record.ActiveSeconds),

                SeedingDuration = TimeSpan.FromSeconds(record.SeedingSeconds)

            }

        };

        // Set progress-related stats

        if (resumeData != null)

        {

            managed.Statistics.PiecesCompleted = resumeData.GetCompletedPieceCount();

            managed.Statistics.TotalDone = (long)(record.TotalSize * record.Progress);

            managed.Statistics.TotalWantedDone = managed.Statistics.TotalDone; // Same as TotalDone when all files wanted

        }

        else

        {

            managed.Statistics.TotalDone = (long)(record.TotalSize * record.Progress);

            managed.Statistics.TotalWantedDone = managed.Statistics.TotalDone; // Same as TotalDone when all files wanted

            managed.Statistics.PiecesCompleted = (int)(record.PieceCount * record.Progress);

        }

        // CRITICAL: Sync IsFinished and IsSeeding flags to Statistics

        // These must be restored for correct state display and resume behavior

        managed.Statistics.IsFinished = record.IsFinished;

        managed.Statistics.IsSeeding = record.IsSeed;

        // Ensure finished state is consistent with seeding state or near-complete progress

        if (!managed.IsFinished && (record.TransferPhase == "Seeding" || record.IsSeed || record.Progress >= 0.99))

        {

            managed.IsFinished = true;

            managed.Statistics.IsFinished = true;

            managed.Statistics.IsSeeding = true;

        }

        // User paused if intent is paused

        managed.UserPaused = record.UserIntent == "Paused";

        // Restore orthogonal state dimensions from DB if present (v6+ schema).

        // If not present, derive from legacy State field (backward compat).

        if (!string.IsNullOrEmpty(record.TransferPhase))

        {

            var restoredStatus = new TorrentStatus

            {

                Phase = Enum.TryParse<TransferPhase>(record.TransferPhase, out var p) ? p : TransferPhase.Idle,

                FileOp = Enum.TryParse<FileOperation>(record.FileOperation ?? "None", out var fo) ? fo : FileOperation.None,

                Intent = Enum.TryParse<UserIntent>(record.UserIntent ?? "Paused", out var ui) ? ui : UserIntent.Paused,

                Error = record.ErrorMessage != null ? new TorrentError { Message = record.ErrorMessage } : (TorrentError?)null,

                MissingFiles = string.Equals(record.Health, "MissingFiles", StringComparison.OrdinalIgnoreCase),

                IsAutoManaged = record.AutoManaged,

                IsFinished = record.IsFinished,

                IsSeed = record.IsSeed,

            };

            managed.StateController.PostRestore(
                restoredStatus.Phase, restoredStatus.Intent,
                FileOperation.None,  // never restore active file ops after restart
                restoredStatus.Error, restoredStatus.MissingFiles,
                restoredStatus.IsAutoManaged);

        }

        // Restore file priorities from resume data so they're applied when engine starts.

        // This ensures skipped files stay skipped across app restarts (libtorrent persistence model).

        if (resumeData?.FilePriorities != null && resumeData.FilePriorities.Count > 0)

        {

            var fileCount = record.FileCount > 0 ? record.FileCount : (resumeData.FilePriorities.Keys.Max() + 1);

            var priorities = new FilePriority[fileCount];

            for (int i = 0; i < fileCount; i++)

                priorities[i] = FilePriority.Normal;

            foreach (var (index, priority) in resumeData.FilePriorities)

            {

                if (index >= 0 && index < fileCount)

                    priorities[index] = (FilePriority)priority;

            }

            managed.PendingFilePriorities = priorities;

        }

        return managed;

    }

    private static TransferPhase ParsePhase(string? phase)

    {

        if (string.IsNullOrEmpty(phase)) return TransferPhase.Idle;

        return Enum.TryParse<TransferPhase>(phase, ignoreCase: true, out var result)

            ? result

            : TransferPhase.Idle;

    }

    private static UserIntent ParseIntent(string? intent)

    {

        if (string.IsNullOrEmpty(intent)) return UserIntent.Paused;

        return Enum.TryParse<UserIntent>(intent, ignoreCase: true, out var result)

            ? result

            : UserIntent.Paused;

    }

    #endregion

    #region Snapshot

    /// <summary>

    /// Creates an immutable snapshot merging engine stats + orchestrator metadata.

    /// Called by the stats update timer for each changed torrent.

    /// </summary>

    public TorrentSnapshot CreateSnapshot()

    {

        var stats = Statistics;

        var status = GetStatus();

        return new TorrentSnapshot

        {

            // Identity

            InfoHash = InfoHash,

            InfoHashV2 = null,

            Name = Name,

            TorrentVersionValue = Bencode.Torrents.TorrentVersion.V1,

            // State

            Status = status,

            // Progress

            TotalSize = stats.TotalSize,

            TotalWanted = stats.TotalWanted,

            TotalWantedDone = stats.TotalWantedDone,

            PiecesCompleted = stats.PiecesCompleted,

            TotalPieces = stats.TotalPieces,

            VerifiedProgress = stats.VerifiedProgress,

            PendingPieces = stats.PendingPieces,

            // Rates

            PayloadDownloadRate = (int)stats.PayloadDownloadRate,

            PayloadUploadRate = (int)stats.PayloadUploadRate,

            SmoothedPayloadDownloadRate = stats.SmoothedPayloadDownloadRate,

            TotalDownloadRate = (int)stats.DownloadRate,

            TotalUploadRate = (int)stats.UploadRate,

            // Byte counters

            SessionPayloadDownloaded = stats.AllTimeDownloaded,

            SessionPayloadUploaded = stats.AllTimeUploaded,

            TotalUploaded = stats.AllTimeUploaded,

            // Peers

            ConnectedPeers = stats.ConnectedPeers,

            ConnectedSeeds = stats.ConnectedSeeds,

            TotalPeers = stats.KnownPeers,

            TotalSeeds = stats.TrackerSeeders,

            // Health & endgame

            Availability = stats.Availability,

            IsEndgame = stats.IsEndgame,

            EndgameWastedBytes = stats.EndgameWastedBytes,

            EndgameDuplicateBlocks = stats.EndgameDuplicateBlocks,

            IsSeeding = IsSeed,

            IsFinished = IsFinished,

            // Time

            AddedOn = AddedTime,

            CompletedOn = CompletedTime,

            ActiveDuration = stats.ActiveDuration,

            SeedingDuration = stats.SeedingDuration,

            // Storage & queue

            SavePath = SavePath,

            QueuePosition = QueuePosition,

            IsForceResumed = !IsAutoManaged,

            // Category & tags

            CategoryId = CategoryId,

            CategoryName = CategoryName,

            Tags = Tags?.Select(t => t.Name).ToList().AsReadOnly()

                ?? (IReadOnlyList<string>)Array.Empty<string>(),

            // Error

            ErrorMessage = ErrorMessage,

        };

    }

    #endregion

    #region ToView

    /// <summary>
    /// Creates a ManagedTorrentView DTO for consumption by Desktop detail views.
    /// Captures all state, stats, and engine-sourced detail lists at a point in time.
    /// </summary>
    internal ManagedTorrentView ToView()
    {
        var stats = Statistics;
        var status = GetStatus();
        var engine = Engine;
        var torrentMeta = Torrent;

        // Engine-level metadata
        var maxConn = 0;
        long dlLimit = 0, ulLimit = 0;
        bool isDlLimited = false, isUlLimited = false;

        if (engine != null)
        {
            maxConn = engine.PeerManagerInternal?.MaxConnections ?? 0;
            var limiter = engine.BandwidthLimiterInternal;
            if (limiter != null)
            {
                isDlLimited = limiter.IsDownloadLimited;
                isUlLimited = limiter.IsUploadLimited;
                dlLimit = isDlLimited ? limiter.EffectiveDownloadLimit : 0;
                ulLimit = isUlLimited ? limiter.EffectiveUploadLimit : 0;
            }
        }

        return new ManagedTorrentView
        {
            // Identity
            InfoHash = InfoHash,
            Name = Name,

            // Metadata
            Creator = torrentMeta?.CreatedBy,
            Comment = torrentMeta?.Comment,
            CreationDate = torrentMeta?.CreationDate?.DateTime,
            IsPrivate = SafeGetIsPrivate(torrentMeta),
            Source = torrentMeta?.Info?.Source,
            DisplayName = null, // Cannot be wired here: ManagedTorrent holds no reference to TorrentSettings.
                               // DisplayName is a user-configurable label stored in per-torrent settings,
                               // which is resolved externally via SettingsResolver. The caller (e.g. service layer)
                               // must overlay this field after calling ToView().
            PieceSize = stats.TotalSize > 0 && stats.TotalPieces > 0
                ? stats.TotalSize / stats.TotalPieces : 0,
            PieceCount = stats.TotalPieces,
            FileCount = torrentMeta?.Info?.Files?.Count ?? 0,
            TotalSize = stats.TotalSize,

            // State
            Status = status,
            ErrorMessage = ErrorMessage,
            IsFinished = IsFinished,
            IsSeed = IsSeed,
            IsAutoManaged = IsAutoManaged,
            SequentialDownload = SequentialDownload,
            FirstLastPiecePriority = FirstLastPiecePriority,

            // Progress / Rates
            Progress = Progress,
            Downloaded = Downloaded,
            Uploaded = Uploaded,
            Ratio = Ratio,
            DownloadRate = DownloadRate,
            UploadRate = UploadRate,

            // Stats (detailed)
            PiecesCompleted = stats.PiecesCompleted,
            TotalPieces = stats.TotalPieces,
            Availability = stats.Availability,
            PayloadDownloadRate = stats.PayloadDownloadRate,
            PayloadUploadRate = stats.PayloadUploadRate,
            SmoothedPayloadDownloadRate = stats.SmoothedPayloadDownloadRate,
            AllTimeDownloaded = stats.AllTimeDownloaded,
            AllTimeUploaded = stats.AllTimeUploaded,
            BytesRemaining = stats.BytesRemaining,
            TotalWastedBytes = stats.TotalWastedBytes,
            StatsRatio = stats.Ratio,
            ConnectedSeeds = stats.ConnectedSeeds,
            ConnectedPeers = stats.ConnectedPeers,
            TrackerSeeders = stats.TrackerSeeders,
            TrackerLeechers = stats.TrackerLeechers,
            ActiveDuration = stats.ActiveDuration,
            SeedingDuration = stats.SeedingDuration,
            ReannounceIn = stats.ReannounceIn,
            LastSeenComplete = stats.LastSeenComplete,

            // Engine
            IsEngineRunning = engine != null,
            MaxConnections = maxConn,
            DownloadBandwidthLimit = dlLimit,
            UploadBandwidthLimit = ulLimit,
            IsDownloadLimited = isDlLimited,
            IsUploadLimited = isUlLimited,

            // Time
            AddedTime = AddedTime,
            CompletedTime = CompletedTime,
            LastActiveTime = LastActiveTime,

            // Storage
            SavePath = SavePath,
            QueuePosition = QueuePosition,

            // Category / Tags
            CategoryId = CategoryId,
            CategoryName = CategoryName,
            Tags = Tags?.Select(t => t.Name).ToList().AsReadOnly()
                ?? (IReadOnlyList<string>)Array.Empty<string>(),

            // Magnet
            IsMagnetLink = IsMagnetLink,
            HasMetadata = HasMetadata,
            MetadataProgress = MetadataProgress,

            // Nested lists — populated from engine internals
            Trackers = BuildTrackerViews(engine),
            Peers = BuildPeerViews(engine),
            Files = BuildFileViews(engine),
            WebSeeds = BuildWebSeedViews(engine),
        };
    }

    private static bool SafeGetIsPrivate(Bencode.Torrents.Torrent? torrentMeta)
    {
        try { return torrentMeta?.Info?.IsPrivate ?? false; }
        catch { return false; }
    }

    private static IReadOnlyList<TrackerInfoView> BuildTrackerViews(TorrentEngine? engine)
    {
        var trackerStats = engine?.TrackerManagerInternal?.GetAllTrackerStatistics();
        if (trackerStats == null || trackerStats.Count == 0)
            return Array.Empty<TrackerInfoView>();

        var result = new List<TrackerInfoView>(trackerStats.Count);
        foreach (var (url, ts) in trackerStats)
        {
            string status;
            if (!ts.IsAvailable) status = "Disabled";
            else if (ts.ConsecutiveFailures > 0) status = "Not working";
            else if (ts.TotalAnnounces == 0) status = "Not contacted";
            else if (ts.NextScheduledAnnounce.HasValue &&
                     ts.NextScheduledAnnounce.Value <= DateTime.UtcNow.AddSeconds(5))
                status = "Updating";
            else status = "Working";

            result.Add(new TrackerInfoView
            {
                Url = url,
                Tier = ts.Tier,
                Status = status,
                Peers = ts.LastPeersReceived,
                Seeds = ts.LastSeeders,
                Leeches = ts.LastLeechers,
                ResponseTime = ts.LastResponseTime > TimeSpan.Zero
                    ? $"{ts.LastResponseTime.TotalMilliseconds:F0} ms"
                    : "-",
            });
        }
        return result;
    }

    private IReadOnlyList<PeerView> BuildPeerViews(TorrentEngine? engine)
    {
        var connected = engine?.PeerManagerInternal?.ConnectedPeers;
        if (connected == null || connected.Count == 0)
            return Array.Empty<PeerView>();

        var snapshot = connected.ToList();
        var stats = engine!.TorrentStatisticsInternal;
        var totalPieces = Statistics.TotalPieces;
        var result = new List<PeerView>(snapshot.Count);

        foreach (var peer in snapshot)
        {
            var dlRate = stats?.GetPeerPayloadDownloadRate(peer) ?? 0;
            var ulRate = stats?.GetPeerPayloadUploadRate(peer) ?? 0;

            // Build flags string
            var flags = "";
            if (peer.IsInterested && !peer.IsChoked) flags += "D";
            else if (peer.IsInterested && peer.IsChoked) flags += "d";
            if (peer.PeerIsInterested && !peer.IsChoking) flags += "U";
            else if (peer.PeerIsInterested && peer.IsChoking) flags += "u";
            if (peer.IsSnubbed) flags += "S";
            if (peer.IsIncoming) flags += "I";
            if (peer.IsEncrypted) flags += "E";

            // Calculate progress from bitfield
            double progress;
            var bitfield = peer.PeerBitfield;
            if (bitfield == null || bitfield.Length == 0)
            {
                progress = peer.IsSeed ? 1.0 : 0.0;
            }
            else if (totalPieces <= 0)
            {
                progress = 0.0;
            }
            else
            {
                int have = 0;
                foreach (var b in bitfield)
                {
                    var v = b;
                    while (v != 0) { have += v & 1; v >>= 1; }
                }
                have = Math.Min(have, totalPieces);
                progress = (double)have / totalPieces;
            }

            result.Add(new PeerView
            {
                IpAddress = peer.PeerInfo.IpAddress?.ToString() ?? "",
                Port = peer.PeerInfo.Port,
                Client = peer.ClientName ?? "Unknown",
                DownloadRate = dlRate,
                UploadRate = ulRate,
                DownloadRateFormatted = dlRate > 0 ? $"{FormatBytesStatic((long)dlRate)}/s" : "-",
                UploadRateFormatted = ulRate > 0 ? $"{FormatBytesStatic((long)ulRate)}/s" : "-",
                Downloaded = stats?.GetPeerPayloadDownloaded(peer) ?? 0,
                Uploaded = stats?.GetPeerPayloadUploaded(peer) ?? 0,
                Progress = progress,
                Flags = flags,
                RoundTripTimeMs = peer.RoundTripTimeMs,
            });
        }
        return result;
    }

    private static IReadOnlyList<FileView> BuildFileViews(TorrentEngine? engine)
    {
        var fileProgress = engine?.FileProgress;
        if (fileProgress == null) return Array.Empty<FileView>();

        var files = fileProgress.Files;
        if (files == null || files.Count == 0) return Array.Empty<FileView>();

        var result = new List<FileView>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            result.Add(new FileView
            {
                Index = f.FileIndex,
                Name = System.IO.Path.GetFileName(f.Path) ?? f.Path,
                Path = f.Path,
                Size = f.Size,
                Progress = f.Progress,
                Priority = f.Priority,
                Availability = f.Availability,
            });
        }
        return result;
    }

    private static IReadOnlyList<WebSeedView> BuildWebSeedViews(TorrentEngine? engine)
    {
        var manager = engine?.WebSeedManagerInternal;
        if (manager == null) return Array.Empty<WebSeedView>();

        var seeds = manager.AllSeeds;
        if (seeds.Count == 0) return Array.Empty<WebSeedView>();

        var stats = engine!.TorrentStatisticsInternal;
        var result = new List<WebSeedView>(seeds.Count);

        foreach (var seed in seeds)
        {
            var dlRate = seed.Connection != null ? (stats?.GetPeerDownloadRate(seed.Connection) ?? 0) : 0;

            string statusStr = seed.Status switch
            {
                Download.WebSeedStatus.Idle => "Idle",
                Download.WebSeedStatus.Active => "Active",
                Download.WebSeedStatus.Banned => "Banned",
                Download.WebSeedStatus.Backoff when seed.NextRetryTime.HasValue =>
                    $"Backoff ({Math.Max(0, (int)(seed.NextRetryTime.Value - DateTime.UtcNow).TotalSeconds)}s)",
                _ => seed.Status.ToString()
            };

            result.Add(new WebSeedView
            {
                Url = seed.Url,
                Type = seed.Type == Download.WebSeedType.BEP19 ? "BEP 19" : "BEP 17",
                Status = statusStr,
                DownloadRate = dlRate,
                DownloadRateFormatted = dlRate > 0 ? $"{FormatBytesStatic((long)dlRate)}/s" : "-",
                Downloaded = seed.BytesDownloaded,
            });
        }
        return result;
    }

    /// <summary>Minimal byte formatter for DTO strings. Avoids Desktop dependency.</summary>
    private static string FormatBytesStatic(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KiB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MiB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GiB";
    }

    #endregion

}
