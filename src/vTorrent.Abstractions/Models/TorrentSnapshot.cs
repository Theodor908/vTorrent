using vTorrent.Bencode.Torrents;
using vTorrent.Abstractions.Enums;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// Immutable point-in-time snapshot of all torrent data.
/// Merges engine stats + orchestrator metadata + persistence data.
/// This is the single DTO that consumers (Desktop, future Server) receive.
/// </summary>
public record TorrentSnapshot
{
    // Identity
    public string InfoHash { get; init; } = "";
    public string? InfoHashV2 { get; init; }
    public string Name { get; init; } = "";
    public string? DisplayName { get; init; }
    public TorrentVersion TorrentVersionValue { get; init; }

    // Orthogonal state (from state machine — composed, not flattened)
    public TorrentStatus Status { get; init; }

    // Progress
    public long TotalSize { get; init; }
    public long TotalWanted { get; init; }
    public long TotalWantedDone { get; init; }
    public int PiecesCompleted { get; init; }
    public int TotalPieces { get; init; }
    public double VerifiedProgress { get; init; }
    public int PendingPieces { get; init; }

    // Rates (raw — formatting is consumer's job)
    public int PayloadDownloadRate { get; init; }
    public int PayloadUploadRate { get; init; }
    public double SmoothedPayloadDownloadRate { get; init; }
    public int TotalDownloadRate { get; init; }
    public int TotalUploadRate { get; init; }

    // Byte counters
    public long SessionPayloadDownloaded { get; init; }
    public long SessionPayloadUploaded { get; init; }
    public long TotalUploaded { get; init; }

    // Peers
    public int ConnectedPeers { get; init; }
    public int ConnectedSeeds { get; init; }
    public int TotalPeers { get; init; }
    public int TotalSeeds { get; init; }

    // Health & endgame
    public float Availability { get; init; }
    public bool IsEndgame { get; init; }
    public long EndgameWastedBytes { get; init; }
    public int EndgameDuplicateBlocks { get; init; }
    public bool IsSeeding { get; init; }
    public bool IsFinished { get; init; }

    // Time
    public DateTime AddedOn { get; init; }
    public DateTime? CompletedOn { get; init; }
    public TimeSpan ActiveDuration { get; init; }
    public TimeSpan SeedingDuration { get; init; }

    // Storage & queue
    public string SavePath { get; init; } = "";
    public int QueuePosition { get; init; }
    public bool IsForceResumed { get; init; }

    // Category & tags (tags as strings — Tag ID is a persistence concern)
    public int? CategoryId { get; init; }
    public string? CategoryName { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    // Error
    public string? ErrorMessage { get; init; }
}
