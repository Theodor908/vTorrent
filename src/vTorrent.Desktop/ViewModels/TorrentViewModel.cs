using CommunityToolkit.Mvvm.ComponentModel;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Records;
using vTorrent.Core;
using vTorrent.Desktop.Formatting;
using vTorrent.Core.State;
using vTorrent.Storage;

namespace vTorrent.Desktop.ViewModels;

/// <summary>
/// Observable wrapper around a Core TorrentSnapshot.
/// All display logic (colors, formatting, display state) lives here exclusively.
/// </summary>
public partial class TorrentViewModel : ObservableObject
{
    private TorrentSnapshot _snapshot;

    // Smooths the display-state stream: transient startup phases (Allocating/Verifying/Connecting)
    // that flash by in milliseconds are suppressed; a phase that genuinely persists still appears.
    private readonly Services.DisplayStateDebouncer _displayDebouncer = new();
    private TorrentDisplayState _displayState;

    public TorrentViewModel(TorrentSnapshot snapshot)
    {
        _snapshot = snapshot;
        _displayState = ComputeDisplayState();
    }

    // --- Data: delegated to snapshot (raw values) ---
    public string InfoHash => _snapshot.InfoHash;
    public string? InfoHashV2 => _snapshot.InfoHashV2;
    public string Name => _snapshot.Name;
    public string? DisplayName => _snapshot.DisplayName;

    /// <summary>Display name if set, otherwise torrent name.</summary>
    public string EffectiveDisplayName => DisplayName ?? Name;

    public double Progress
    {
        get
        {
            // During checking, show checking progress instead of download progress
            if (_snapshot.Status.Phase == TransferPhase.CheckingFiles && _snapshot.Status.FileOpProgress > 0)
                return _snapshot.Status.FileOpProgress;

            return _snapshot.TotalWanted > 0
                ? (double)_snapshot.TotalWantedDone / _snapshot.TotalWanted
                : 0;
        }
    }
    public double VerifiedProgress => _snapshot.VerifiedProgress;
    public int PendingPieces => _snapshot.PendingPieces;
    public int DownloadRate => _snapshot.PayloadDownloadRate;
    public double SmoothedDownloadRate => _snapshot.SmoothedPayloadDownloadRate;
    public int UploadRate => _snapshot.PayloadUploadRate;
    public long TotalDone => _snapshot.TotalWantedDone;
    public long TotalSize => _snapshot.TotalSize;
    public long TotalWanted => _snapshot.TotalWanted;
    public int CompletedPieces => _snapshot.PiecesCompleted;
    public int TotalPieces => _snapshot.TotalPieces;
    public long Uploaded => _snapshot.TotalUploaded;
    public int ConnectedPeers => _snapshot.ConnectedPeers;
    public int ConnectedSeeds => _snapshot.ConnectedSeeds;
    public int TotalPeers => _snapshot.TotalPeers;
    public int TotalSeeds => _snapshot.TotalSeeds;
    public float Availability => _snapshot.Availability;
    public bool IsEndgame => _snapshot.IsEndgame;
    public long EndgameWastedBytes => _snapshot.EndgameWastedBytes;
    public int EndgameDuplicateBlocks => _snapshot.EndgameDuplicateBlocks;
    public int QueuePosition => _snapshot.QueuePosition;
    public string SavePath => _snapshot.SavePath;
    public DateTime AddedOn => _snapshot.AddedOn;
    public DateTime? CompletedOn => _snapshot.CompletedOn;
    public TimeSpan ActiveDuration => _snapshot.ActiveDuration;
    public TimeSpan SeedingDuration => _snapshot.SeedingDuration;
    public TorrentStatus TorrentStatus => _snapshot.Status;
    public int? CategoryId => _snapshot.CategoryId;
    public string? CategoryName => _snapshot.CategoryName;
    public IReadOnlyList<string> TagNames => _snapshot.Tags;
    public List<Tag> Tags => _snapshot.Tags.Select(n => new Tag { Name = n }).ToList();
    public string? ErrorMessage => _snapshot.ErrorMessage;
    public bool IsForceResumed => _snapshot.IsForceResumed;
    public bool IsSeeding => _snapshot.IsSeeding;
    public bool IsFinished => _snapshot.IsFinished;

    // --- Display state: derived from snapshot (Desktop-only), then debounced so brief
    // startup phases don't flicker before settling. Updated by Update()/the constructor. ---
    public TorrentDisplayState State => _displayState;

    // --- Status display ---
    public string Status => State switch
    {
        TorrentDisplayState.Downloading => "Downloading",
        TorrentDisplayState.Seeding => "Seeding",
        TorrentDisplayState.Paused => "Paused",
        TorrentDisplayState.Allocating => "Allocating",
        TorrentDisplayState.Verifying => "Verifying",
        TorrentDisplayState.Checking => "Checking",
        TorrentDisplayState.Queued => "Queued",
        TorrentDisplayState.Error => "Error",
        TorrentDisplayState.Stalled => "Stalled",
        TorrentDisplayState.Moving => "Moving",
        TorrentDisplayState.MetadataDownloading => "Fetching Metadata",
        TorrentDisplayState.ForcedDownloading => "[F] Downloading",
        TorrentDisplayState.ForcedSeeding => "[F] Seeding",
        TorrentDisplayState.Stopping => "Stopping...",
        TorrentDisplayState.CheckingResumeData => "Checking resume data...",
        TorrentDisplayState.StalledSeeding => "Seeding (Stalled)",
        TorrentDisplayState.MissingFiles => "Missing Files",
        TorrentDisplayState.Connecting => "Connecting...",
        TorrentDisplayState.Stopped => "Stopped",
        _ => "Unknown"
    };

    public string StatusColor => State switch
    {
        TorrentDisplayState.Downloading => "#00d9ff",
        TorrentDisplayState.ForcedDownloading => "#00d9ff",
        TorrentDisplayState.Connecting => "#00d9ff",
        TorrentDisplayState.Seeding => "#10B981",
        TorrentDisplayState.ForcedSeeding => "#10B981",
        TorrentDisplayState.Paused => "#6B7280",
        TorrentDisplayState.Stopping => "#6B7280",
        TorrentDisplayState.Stopped => "#6B7280",
        TorrentDisplayState.Queued => "#6B7280",
        TorrentDisplayState.Allocating => "#8B5CF6",
        TorrentDisplayState.Verifying => "#8B5CF6",
        TorrentDisplayState.Checking => "#8B5CF6",
        TorrentDisplayState.CheckingResumeData => "#8B5CF6",
        TorrentDisplayState.Moving => "#8B5CF6",
        TorrentDisplayState.MetadataDownloading => "#8B5CF6",
        TorrentDisplayState.Stalled => "#F59E0B",
        TorrentDisplayState.StalledSeeding => "#F59E0B",
        TorrentDisplayState.Error => "#EF4444",
        TorrentDisplayState.MissingFiles => "#EF4444",
        _ => "#6B7280"
    };

    public string StatusTooltip => State switch
    {
        TorrentDisplayState.Downloading => "Downloading - Actively receiving data from peers",
        TorrentDisplayState.ForcedDownloading => "Downloading [Forced] - Bypassing queue limits",
        TorrentDisplayState.Seeding => "Seeding - Sharing data with other peers",
        TorrentDisplayState.ForcedSeeding => "Seeding [Forced] - Bypassing queue limits",
        TorrentDisplayState.Paused => "Paused - Transfer is stopped",
        TorrentDisplayState.Allocating => "Allocating - Preparing file structure",
        TorrentDisplayState.Verifying => "Verifying - Checking piece hashes on startup",
        TorrentDisplayState.Checking => "Checking - Rechecking file integrity",
        TorrentDisplayState.Queued => "Queued - Waiting in download queue",
        TorrentDisplayState.Error => string.IsNullOrEmpty(ErrorMessage)
            ? "Error - An error occurred"
            : $"Error - {ErrorMessage}",
        TorrentDisplayState.Stalled => "Stalled - No active peers available",
        TorrentDisplayState.Moving => "Moving - Relocating files to new location",
        TorrentDisplayState.MetadataDownloading => "Fetching Metadata - Downloading torrent info from peers",
        TorrentDisplayState.Stopping => "Stopping - Gracefully shutting down transfer",
        TorrentDisplayState.CheckingResumeData => "Checking Resume Data - Verifying saved state",
        TorrentDisplayState.StalledSeeding => "Seeding (Stalled) - No active leechers available",
        TorrentDisplayState.MissingFiles => "Missing Files - Some files cannot be found on disk",
        TorrentDisplayState.Connecting => "Connecting - Establishing peer connections",
        TorrentDisplayState.Stopped => "Stopped - Transfer is stopped",
        _ => "Unknown state"
    };

    // --- Computed display properties ---
    public int ProgressPercent => (int)(Progress * 100);
    public string ProgressDisplay => $"{Progress:P1}";
    public string ProgressTooltip => $"{ProgressPercent}% - {FormatHelper.FormatBytes(TotalDone)} of {FormatHelper.FormatBytes(TotalWanted > 0 ? TotalWanted : TotalSize)} ({CompletedPieces}/{TotalPieces} pieces)";
    public int VerifiedProgressPercent => (int)(VerifiedProgress * 100);
    public string VerifiedProgressDisplay => $"{VerifiedProgress:P1}";
    public string PendingPiecesDisplay => PendingPieces > 0 ? $"{PendingPieces} pending" : "-";
    public string Size => FormatHelper.FormatBytes(TotalWanted > 0 ? TotalWanted : TotalSize);
    public string SizeDisplay => $"{FormatHelper.FormatBytes(TotalDone)} / {FormatHelper.FormatBytes(TotalWanted > 0 ? TotalWanted : TotalSize)}";
    public string Downloaded => FormatHelper.FormatBytes(TotalDone);
    public string PiecesDisplay => $"{CompletedPieces} / {TotalPieces}";
    public string UploadedDisplay => FormatHelper.FormatBytes(Uploaded);
    public string DownloadSpeed => FormatHelper.FormatSpeed(DownloadRate);
    public string UploadSpeed => FormatHelper.FormatSpeed(UploadRate);
    public double Ratio => TotalDone > 0 ? (double)Uploaded / TotalDone : 0;
    public string RatioDisplay => Ratio.ToString("F2");
    public string PeersDisplay => $"{ConnectedPeers} ({TotalPeers})";
    public string SeedsDisplay => $"{ConnectedSeeds} ({TotalSeeds})";
    public string AddedOnDisplay => AddedOn.ToLocalTime().ToString("g");
    public string CompletedOnDisplay => CompletedOn?.ToLocalTime().ToString("g") ?? "-";
    public string ActiveDurationDisplay => FormatHelper.FormatDuration(ActiveDuration);
    public string SeedingDurationDisplay => FormatHelper.FormatDuration(SeedingDuration);
    public double AvailabilityDisplay => Availability;
    public string AvailabilityDisplayStr => $"{Availability:F2}";
    public string EndgameWastedDisplay => EndgameWastedBytes > 0 ? FormatHelper.FormatBytes(EndgameWastedBytes) : "-";
    public string StatusDetail => IsEndgame ? $"{Status} (Endgame)" : Status;
    public bool HasCategory => CategoryId.HasValue;
    public bool HasTags => TagNames.Count > 0;
    public string TagsDisplay => TagNames.Count > 0 ? string.Join(", ", TagNames) : "-";

    public string ProtocolVersion => _snapshot.TorrentVersionValue switch
    {
        vTorrent.Bencode.Torrents.TorrentVersion.V1 => "v1",
        vTorrent.Bencode.Torrents.TorrentVersion.V2 => "v2",
        vTorrent.Bencode.Torrents.TorrentVersion.Hybrid => "Hybrid v1+v2",
        _ => "Unknown"
    };
    public string InfoHashV1Display => string.IsNullOrEmpty(InfoHash) ? "N/A" : InfoHash;
    public string InfoHashV2Display => string.IsNullOrEmpty(InfoHashV2) ? "N/A" : InfoHashV2;

    // --- ETA ---
    public TimeSpan? ETA
    {
        get
        {
            var rate = SmoothedDownloadRate > 0 ? SmoothedDownloadRate : DownloadRate;
            if (State is not (TorrentDisplayState.Downloading or TorrentDisplayState.ForcedDownloading) || rate <= 0)
                return null;
            var effectiveSize = TotalWanted > 0 ? TotalWanted : TotalSize;
            var remaining = effectiveSize - TotalDone;
            if (remaining <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(remaining / rate);
        }
    }

    public string ETADisplay
    {
        get
        {
            var eta = ETA;
            if (eta == null) return "-";
            if (eta == TimeSpan.Zero) return "Complete";
            var value = eta.Value;
            if (value.TotalDays >= 1)
                return $"{(int)value.TotalDays}d {value.Hours}h";
            if (value.TotalHours >= 1)
                return $"{(int)value.TotalHours}h {value.Minutes}m";
            if (value.TotalMinutes >= 1)
                return $"{(int)value.TotalMinutes}m {value.Seconds}s";
            return $"{(int)value.TotalSeconds}s";
        }
    }

    // --- UI-only state ---
    [ObservableProperty]
    private bool _isSelected;

    // --- Bulk update: called from TorrentManagerService ---
    public void Update(TorrentSnapshot snapshot)
    {
        _snapshot = snapshot;
        _displayState = ComputeDisplayState();
        OnPropertyChanged(string.Empty);  // refresh all bindings
    }

    // Derive the raw display state from the current snapshot, then run it through the debouncer
    // so transitional phases that last less than the debounce window are never shown.
    private TorrentDisplayState ComputeDisplayState()
    {
        var derived = Services.DisplayStateDeriver.Derive(
            _snapshot.Status,
            _snapshot.PayloadDownloadRate,
            _snapshot.PayloadUploadRate,
            _snapshot.ConnectedPeers);
        return _displayDebouncer.Resolve(derived, System.Environment.TickCount64);
    }
}

/// <summary>
/// Display states for torrents — Desktop-only. Core never sees this.
/// </summary>
public enum TorrentDisplayState
{
    Downloading,
    Seeding,
    Paused,
    Queued,
    Error,
    Stalled,
    Allocating,
    Verifying,
    Checking,
    Moving,
    MetadataDownloading,
    ForcedDownloading,
    ForcedSeeding,
    Stopping,
    CheckingResumeData,
    StalledSeeding,
    MissingFiles,
    Connecting,
    Stopped,
}
