using System;
using vTorrent.Abstractions.Enums;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// Immutable point-in-time snapshot of torrent engine state.
/// Modeled after libtorrent's torrent_status struct.
/// This is the single source of truth for live engine data.
/// </summary>
public record TorrentStatusSnapshot
{
    // Progress
    public long TotalSize { get; init; }
    public long TotalWanted { get; init; }
    public long TotalWantedDone { get; init; }
    public int PiecesCompleted { get; init; }
    public int TotalPieces { get; init; }
    public double VerifiedProgress { get; init; }

    // Rates (payload only - what UI displays)
    public int PayloadDownloadRate { get; init; }
    public int PayloadUploadRate { get; init; }
    public double SmoothedPayloadDownloadRate { get; init; }

    // Rates (total including protocol overhead - for SessionOverviewViewModel)
    public int TotalDownloadRate { get; init; }
    public int TotalUploadRate { get; init; }

    // Byte counters
    public long SessionPayloadDownloaded { get; init; }
    public long SessionPayloadUploaded { get; init; }
    public long SessionDownloaded { get; init; }
    public long SessionUploaded { get; init; }
    public long VerifiedDownloaded { get; init; }

    // Peers
    public int ConnectedPeers { get; init; }
    public int ConnectedSeeds { get; init; }

    // Waste & health
    public long FailedBytes { get; init; }
    public long EndgameWastedBytes { get; init; }
    public int EndgameDuplicateBlocks { get; init; }
    public bool IsEndgame { get; init; }
    public float Availability { get; init; }

    // Tracker
    public DateTime? LastAnnounce { get; init; }
    public int AnnounceInterval { get; init; }
    public TimeSpan? TimeToNextAnnounce { get; init; }

    // Orthogonal status (new — preferred over State)
    public TorrentStatus Status { get; init; }
}
