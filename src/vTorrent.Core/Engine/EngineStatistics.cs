using System;
using System.Collections;
using vTorrent.Core.Interfaces;
using vTorrent.Core.Session;
using vTorrent.Core.PieceIO;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Engine;

/// <summary>
/// Provides a cohesive view of all TorrentEngine statistics.
/// Extracted from TorrentEngine as part of god class decomposition (Phase 5, Task 5.4).
/// All properties are live delegates to the engine's internal sub-components.
/// </summary>
public class EngineStatistics
{
    private readonly TorrentEngine _engine;

    internal EngineStatistics(TorrentEngine engine)
    {
        _engine = engine;
    }

    #region Download Progress

    public double Progress => _engine.DownloadCoordinatorInternal?.Progress ?? 0;
    public int PiecesCompleted => _engine.DownloadCoordinatorInternal?.PiecesCompleted ?? 0;
    public long BytesInProgress => _engine.DownloadCoordinatorInternal?.BytesInProgress ?? 0;
    public long BytesEffective => _engine.DownloadCoordinatorInternal?.BytesEffective ?? 0;
    public long BytesRemaining => _engine.DownloadCoordinatorInternal?.BytesRemaining ?? _engine.TotalSize;

    #endregion

    #region Transfer Stats (from ChokingManager)

    public long TotalUploaded => _engine.ChokingManagerInternal?.TotalUploaded ?? 0;
    public long TotalDownloaded => _engine.ChokingManagerInternal?.TotalDownloaded ?? 0;
    public int UnchokedPeers => _engine.ChokingManagerInternal?.UnchokedPeerCount ?? 0;

    #endregion

    #region Peers

    public int ConnectedPeers => _engine.PeerManagerInternal?.ConnectedPeerCount ?? 0;
    public int ConnectedSeeds => _engine.DownloadCoordinatorInternal?.ConnectedSeeds ?? 0;
    public int TotalSeeders => _engine.TrackerManagerInternal?.TotalSeeders ?? 0;
    public int TotalLeechers => _engine.TrackerManagerInternal?.TotalLeechers ?? 0;
    public bool IsSeederSwarm => _engine.SeederSwarmDetectorInternal?.IsSeederSwarm ?? false;

    #endregion

    #region Tracker

    public DateTime? LastAnnounce => _engine.TrackerManagerInternal?.LastSuccessfulAnnounce;
    public int AnnounceInterval => _engine.TrackerManagerInternal?.NextAnnounceInterval ?? 0;
    public TimeSpan? TimeToNextAnnounce => _engine.TrackerManagerInternal?.TimeToNextAnnounce;

    #endregion

    #region File Progress & Availability

    public FileProgressTracker FileProgress => _engine.FileProgressTrackerInternal;
    public float Availability => _engine.FileProgressTrackerInternal?.GetOverallAvailability() ?? 0f;

    #endregion

    #region Bandwidth Stats (from TorrentStatistics)

    public long BytesDownloaded => (_engine.TorrentStatisticsInternal as IStatisticsTracker)?.TotalDownloaded ?? 0;
    public long BytesUploaded => (_engine.TorrentStatisticsInternal as IStatisticsTracker)?.TotalUploaded ?? 0;
    public double DownloadRate => _engine.TorrentStatisticsInternal?.DownloadRate ?? 0;
    public double UploadRate => _engine.TorrentStatisticsInternal?.UploadRate ?? 0;

    // Payload-only stats (actual file data, excludes protocol overhead)
    public long PayloadDownloaded => (_engine.TorrentStatisticsInternal as IStatisticsTracker)?.PayloadDownloaded ?? 0;
    public long PayloadUploaded => (_engine.TorrentStatisticsInternal as IStatisticsTracker)?.PayloadUploaded ?? 0;
    public double PayloadDownloadRate => _engine.TorrentStatisticsInternal?.PayloadDownloadRate ?? 0;
    public double PayloadUploadRate => _engine.TorrentStatisticsInternal?.PayloadUploadRate ?? 0;

    // Smoothed rate for ETA calculations - decays gradually instead of dropping to 0
    public double SmoothedPayloadDownloadRate => _engine.TorrentStatisticsInternal?.SmoothedPayloadDownloadRate ?? 0;

    // Verified stats (only counts hash-verified pieces written to disk)
    public long VerifiedDownloaded => _engine.TorrentStatisticsInternal?.VerifiedDownloaded ?? 0;
    public double VerifiedDownloadRate => _engine.TorrentStatisticsInternal?.VerifiedDownloadRate ?? 0;

    #endregion

    #region Endgame Mode

    public bool IsEndgameMode => _engine.DownloadCoordinatorInternal?.IsEndgameMode ?? false;
    public long EndgameWastedBytes => _engine.TorrentStatisticsInternal?.EndgameWastedBytes ?? 0;
    public int EndgameDuplicateBlocks => _engine.TorrentStatisticsInternal?.EndgameDuplicateBlocks ?? 0;
    public long FailedBytes => _engine.TorrentStatisticsInternal?.FailedBytes ?? 0;

    #endregion

    #region Snapshots

    /// <summary>
    /// Returns an immutable snapshot of the engine's current state.
    /// </summary>
    public TorrentStatusSnapshot GetStatus()
    {
        return new TorrentStatusSnapshot
        {
            TotalSize = _engine.TotalSize,
            TotalWanted = _engine.FileProgressTrackerInternal?.GetTotalWantedBytes() ?? _engine.TotalSize,
            TotalWantedDone = _engine.FileProgressTrackerInternal?.GetWantedBytesCompleted() ?? 0,
            PiecesCompleted = PiecesCompleted,
            TotalPieces = _engine.PieceCount,
            VerifiedProgress = _engine.PieceCount > 0 ? (double)PiecesCompleted / _engine.PieceCount : 0.0,

            PayloadDownloadRate = (int)PayloadDownloadRate,
            PayloadUploadRate = (int)PayloadUploadRate,
            SmoothedPayloadDownloadRate = SmoothedPayloadDownloadRate,

            TotalDownloadRate = (int)DownloadRate,
            TotalUploadRate = (int)UploadRate,

            SessionPayloadDownloaded = PayloadDownloaded,
            SessionPayloadUploaded = PayloadUploaded,
            SessionDownloaded = TotalDownloaded,
            SessionUploaded = (_engine.TorrentStatisticsInternal as IStatisticsTracker)?.TotalUploaded ?? 0,
            VerifiedDownloaded = VerifiedDownloaded,

            ConnectedPeers = ConnectedPeers,
            ConnectedSeeds = ConnectedSeeds,

            FailedBytes = FailedBytes,
            EndgameWastedBytes = EndgameWastedBytes,
            EndgameDuplicateBlocks = EndgameDuplicateBlocks,
            IsEndgame = IsEndgameMode,
            Availability = Availability,

            LastAnnounce = LastAnnounce,
            AnnounceInterval = AnnounceInterval,
            TimeToNextAnnounce = TimeToNextAnnounce,

            Status = new TorrentStatus
            {
                Phase = _engine.Phase,
                Error = _engine.EngineError,
            }
        };
    }

    /// <summary>
    /// Gets the current piece completion state as a BitArray for saving to resume data.
    /// </summary>
    public BitArray? GetPieceBitfield()
    {
        var bitfield = _engine.LocalBitfieldInternal;
        if (bitfield == null)
            return null;

        var bitArray = new BitArray(bitfield.PieceCount);
        for (int i = 0; i < bitfield.PieceCount; i++)
        {
            bitArray[i] = bitfield.HasPiece(i);
        }
        return bitArray;
    }

    #endregion
}
