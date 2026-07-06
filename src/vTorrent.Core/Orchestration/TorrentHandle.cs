using System;
using System.Collections.Generic;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Records;
using vTorrent.Core.Session;
using vTorrent.Core.State;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Non-owning handle to a managed torrent.
/// Provides read-only access to torrent state and operations.
/// Similar to libtorrent's torrent_handle.
/// </summary>
public class TorrentHandle
{
    private readonly ManagedTorrent _torrent;

    internal TorrentHandle(ManagedTorrent torrent)
    {
        _torrent = torrent ?? throw new ArgumentNullException(nameof(torrent));
    }

    #region Identity

    /// <summary>
    /// Info hash (hex string)
    /// </summary>
    public string InfoHash => _torrent.InfoHash;

    /// <summary>
    /// Torrent name
    /// </summary>
    public string Name => _torrent.Name;

    #endregion

    #region State

    /// <summary>
    /// Current torrent status (Phase, Intent, Health dimensions)
    /// </summary>
    public TorrentStatus Status => _torrent.GetStatus();

    /// <summary>
    /// Error message (when State == Error)
    /// </summary>
    public string? ErrorMessage => _torrent.ErrorMessage;

    /// <summary>
    /// Whether torrent is finished downloading
    /// </summary>
    public bool IsFinished => _torrent.IsFinished;

    /// <summary>
    /// Whether torrent is a seed
    /// </summary>
    public bool IsSeed => _torrent.IsSeed;

    /// <summary>
    /// Whether torrent is paused
    /// </summary>
    public bool IsPaused => _torrent.GetStatus().Intent == UserIntent.Paused;

    /// <summary>
    /// Whether torrent has an active engine
    /// </summary>
    public bool IsActive => _torrent.IsEngineRunning;

    #endregion

    #region Progress

    /// <summary>
    /// Download progress (0.0 to 1.0)
    /// </summary>
    public double Progress => _torrent.Progress;

    /// <summary>
    /// Progress as percentage (0-100)
    /// </summary>
    public int ProgressPercent => (int)(_torrent.Progress * 100);

    /// <summary>
    /// Total size in bytes
    /// </summary>
    public long TotalSize => _torrent.TotalSize;

    /// <summary>
    /// Total wanted bytes (respects file priorities, excludes skipped files).
    /// Follows libtorrent's total_wanted.
    /// </summary>
    public long TotalWanted => _torrent.Statistics.TotalWanted;

    /// <summary>
    /// Total wanted bytes completed.
    /// </summary>
    public long TotalWantedDone => _torrent.Statistics.TotalWantedDone;

    /// <summary>
    /// Downloaded bytes
    /// </summary>
    public long Downloaded => _torrent.Downloaded;

    /// <summary>
    /// Uploaded bytes (all time)
    /// </summary>
    public long Uploaded => _torrent.Uploaded;

    /// <summary>
    /// Share ratio
    /// </summary>
    public double Ratio => _torrent.Ratio;

    /// <summary>
    /// Pieces completed
    /// </summary>
    public int PiecesCompleted => _torrent.Statistics.PiecesCompleted;

    /// <summary>
    /// Total pieces
    /// </summary>
    public int TotalPieces => _torrent.Statistics.TotalPieces;

    #endregion

    #region Transfer Rates

    /// <summary>
    /// Current download rate (bytes/sec) - includes protocol overhead
    /// </summary>
    public int DownloadRate => _torrent.DownloadRate;

    /// <summary>
    /// Current upload rate (bytes/sec) - includes protocol overhead
    /// </summary>
    public int UploadRate => _torrent.UploadRate;

    /// <summary>
    /// Current payload download rate (bytes/sec) - actual file data only
    /// </summary>
    public int PayloadDownloadRate => (int)_torrent.Statistics.PayloadDownloadRate;

    /// <summary>
    /// Current payload upload rate (bytes/sec) - actual file data only
    /// </summary>
    public int PayloadUploadRate => (int)_torrent.Statistics.PayloadUploadRate;

    #endregion

    #region Peers

    /// <summary>
    /// Number of connected peers
    /// </summary>
    public int ConnectedPeers => _torrent.ConnectedPeers;

    /// <summary>
    /// Number of connected seeds
    /// </summary>
    public int ConnectedSeeds => _torrent.ConnectedSeeds;

    #endregion

    #region Queue

    /// <summary>
    /// Queue position
    /// </summary>
    public int QueuePosition => _torrent.QueuePosition;

    /// <summary>
    /// Whether auto-management is enabled
    /// </summary>
    public bool IsAutoManaged => _torrent.IsAutoManaged;

    /// <summary>
    /// Whether user explicitly paused
    /// </summary>
    public bool UserPaused => _torrent.UserPaused;

    #endregion

    #region Timing

    /// <summary>
    /// When torrent was added
    /// </summary>
    public DateTime AddedTime => _torrent.AddedTime;

    /// <summary>
    /// When torrent completed (null if not finished)
    /// </summary>
    public DateTime? CompletedTime => _torrent.CompletedTime;

    /// <summary>
    /// Active duration
    /// </summary>
    public TimeSpan ActiveDuration => _torrent.Statistics.ActiveDuration;

    /// <summary>
    /// Seeding duration
    /// </summary>
    public TimeSpan SeedingDuration => _torrent.Statistics.SeedingDuration;

    #endregion

    #region Storage

    /// <summary>
    /// Save path for downloaded files
    /// </summary>
    public string SavePath => _torrent.SavePath;

    #endregion

    #region Category & Tags

    /// <summary>
    /// Category ID (null if uncategorized)
    /// </summary>
    public int? CategoryId => _torrent.CategoryId;

    /// <summary>
    /// Category name (cached for display)
    /// </summary>
    public string? CategoryName => _torrent.CategoryName;

    /// <summary>
    /// Tags associated with this torrent
    /// </summary>
    public List<Tag> Tags => _torrent.Tags;

    #endregion

    #region Statistics Access

    /// <summary>
    /// Full statistics snapshot
    /// </summary>
    public TorrentStatistics GetStatistics() => _torrent.Statistics.CreateSnapshot();

    /// <summary>
    /// Average download rate over session (bytes/sec)
    /// </summary>
    public double AverageDownloadRate => _torrent.Statistics.AverageDownloadRate;

    /// <summary>
    /// Average upload rate over session (bytes/sec)
    /// </summary>
    public double AverageUploadRate => _torrent.Statistics.AverageUploadRate;

    /// <summary>
    /// Time until next tracker announce
    /// </summary>
    public TimeSpan? ReannounceIn => _torrent.Statistics.ReannounceIn;

    /// <summary>
    /// Tracker announce interval (seconds)
    /// </summary>
    public int AnnounceInterval => _torrent.Statistics.AnnounceInterval;

    /// <summary>
    /// Overall torrent availability
    /// </summary>
    public float Availability => _torrent.Statistics.Availability;

    /// <summary>
    /// Tracker-reported seeders
    /// </summary>
    public int TrackerSeeders => _torrent.Statistics.TrackerSeeders;

    /// <summary>
    /// Tracker-reported leechers
    /// </summary>
    public int TrackerLeechers => _torrent.Statistics.TrackerLeechers;

    /// <summary>
    /// Bytes wasted (failed hash verification)
    /// </summary>
    public long WastedBytes => _torrent.Statistics.FailedBytes;

    /// <summary>
    /// Whether the torrent is in endgame mode (near completion).
    /// </summary>
    public bool IsEndgame => _torrent.Statistics.IsEndgame;

    /// <summary>
    /// Bytes wasted during endgame mode (duplicate blocks).
    /// </summary>
    public long EndgameWastedBytes => _torrent.Statistics.EndgameWastedBytes;

    /// <summary>
    /// Count of duplicate blocks received during endgame.
    /// </summary>
    public int EndgameDuplicateBlocks => _torrent.Statistics.EndgameDuplicateBlocks;

    /// <summary>
    /// Total bytes wasted (hash failures + endgame duplicates + redundant).
    /// </summary>
    public long TotalWastedBytes => _torrent.Statistics.TotalWastedBytes;

    /// <summary>
    /// Smoothed download rate for stable ETA calculations.
    /// </summary>
    public double SmoothedDownloadRate => _torrent.Statistics.SmoothedPayloadDownloadRate;

    /// <summary>
    /// Total bytes ever downloaded
    /// </summary>
    public long AllTimeDownloaded => _torrent.Statistics.AllTimeDownloaded;

    /// <summary>
    /// Total bytes ever uploaded
    /// </summary>
    public long AllTimeUploaded => _torrent.Statistics.AllTimeUploaded;

    /// <summary>
    /// File progress tracker (if available)
    /// </summary>
    public FileProgressTracker? FileProgress => _torrent.Engine?.FileProgress;

    #endregion

    #region Internal Access

    /// <summary>
    /// Get the underlying managed torrent (internal use only)
    /// </summary>
    internal ManagedTorrent GetManagedTorrent() => _torrent;

    #endregion

    #region Equality

    public override bool Equals(object? obj)
    {
        return obj is TorrentHandle handle && handle.InfoHash == InfoHash;
    }

    public override int GetHashCode() => InfoHash.GetHashCode();

    public static bool operator ==(TorrentHandle? left, TorrentHandle? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(TorrentHandle? left, TorrentHandle? right)
    {
        return !(left == right);
    }

    #endregion

    public override string ToString() => $"{Name} [{Status.Phase}]";
}
