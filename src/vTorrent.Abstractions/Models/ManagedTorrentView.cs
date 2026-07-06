using System;
using System.Collections.Generic;
using vTorrent.Abstractions.Enums;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// Read-only DTO exposing ManagedTorrent state to Desktop without leaking Core types.
/// Created by ManagedTorrent.ToView() and consumed by TorrentDetailsViewModel.
/// </summary>
public sealed record ManagedTorrentView
{
    #region Identity

    public string InfoHash { get; init; } = "";
    public string? InfoHashV2 { get; init; }
    public string Name { get; init; } = "";

    #endregion

    #region Metadata (from Torrent object — null-safe for magnet links)

    public string? Creator { get; init; }
    public string? Comment { get; init; }
    public DateTime? CreationDate { get; init; }
    public bool IsPrivate { get; init; }
    public string? Source { get; init; }
    public string? DisplayName { get; init; }
    public long PieceSize { get; init; }
    public int PieceCount { get; init; }
    public int FileCount { get; init; }
    public long TotalSize { get; init; }

    #endregion

    #region State

    public TorrentStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsFinished { get; init; }
    public bool IsSeed { get; init; }
    public bool IsAutoManaged { get; init; }
    public bool SequentialDownload { get; init; }
    public bool FirstLastPiecePriority { get; init; }

    #endregion

    #region Progress / Rates

    public double Progress { get; init; }
    public long Downloaded { get; init; }
    public long Uploaded { get; init; }
    public double Ratio { get; init; }
    public int DownloadRate { get; init; }
    public int UploadRate { get; init; }

    #endregion

    #region Stats (detailed — mirrors TorrentStatistics fields used by DetailsVM)

    public int PiecesCompleted { get; init; }
    public int TotalPieces { get; init; }
    public float Availability { get; init; }
    public double PayloadDownloadRate { get; init; }
    public double PayloadUploadRate { get; init; }
    public double SmoothedPayloadDownloadRate { get; init; }
    public long AllTimeDownloaded { get; init; }
    public long AllTimeUploaded { get; init; }
    public long BytesRemaining { get; init; }
    public long TotalWastedBytes { get; init; }
    public double StatsRatio { get; init; }
    public int ConnectedSeeds { get; init; }
    public int ConnectedPeers { get; init; }
    public int TrackerSeeders { get; init; }
    public int TrackerLeechers { get; init; }
    /// <summary>
    /// BEP 33: Estimated seeds from DHT bloom filter scrape. Null if no scrape data.
    /// </summary>
    public int? DhtSeeds { get; init; }
    /// <summary>
    /// BEP 33: Estimated peers from DHT bloom filter scrape. Null if no scrape data.
    /// </summary>
    public int? DhtPeers { get; init; }
    public TimeSpan ActiveDuration { get; init; }
    public TimeSpan SeedingDuration { get; init; }
    public TimeSpan? ReannounceIn { get; init; }
    public DateTime? LastSeenComplete { get; init; }

    #endregion

    #region Engine

    /// <summary>
    /// Whether the TorrentEngine is currently active (non-null).
    /// </summary>
    public bool IsEngineRunning { get; init; }

    /// <summary>Max connections configured on the peer manager (0 if engine is null).</summary>
    public int MaxConnections { get; init; }

    /// <summary>Per-torrent download bandwidth limit (0 = unlimited).</summary>
    public long DownloadBandwidthLimit { get; init; }

    /// <summary>Per-torrent upload bandwidth limit (0 = unlimited).</summary>
    public long UploadBandwidthLimit { get; init; }

    /// <summary>Whether per-torrent download limiting is active.</summary>
    public bool IsDownloadLimited { get; init; }

    /// <summary>Whether per-torrent upload limiting is active.</summary>
    public bool IsUploadLimited { get; init; }

    #endregion

    #region Peers

    // Aggregated counts are already above (ConnectedPeers, ConnectedSeeds)

    #endregion

    #region Time

    public DateTime AddedTime { get; init; }
    public DateTime? CompletedTime { get; init; }
    public DateTime? LastActiveTime { get; init; }

    #endregion

    #region Storage

    public string SavePath { get; init; } = "";
    public int QueuePosition { get; init; }

    #endregion

    #region Category / Tags

    public int? CategoryId { get; init; }
    public string? CategoryName { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    #endregion

    #region Magnet Link

    public bool IsMagnetLink { get; init; }
    public bool HasMetadata { get; init; }
    public double MetadataProgress { get; init; }

    #endregion

    #region Nested Detail Lists

    /// <summary>Tracker entries. Populated when engine is running.</summary>
    public IReadOnlyList<TrackerInfoView> Trackers { get; init; } = Array.Empty<TrackerInfoView>();

    /// <summary>Connected peers. Populated when engine is running.</summary>
    public IReadOnlyList<PeerView> Peers { get; init; } = Array.Empty<PeerView>();

    /// <summary>File entries. Populated when engine is running and file progress is available.</summary>
    public IReadOnlyList<FileView> Files { get; init; } = Array.Empty<FileView>();

    /// <summary>Web seed (HTTP source) entries. Populated when engine is running.</summary>
    public IReadOnlyList<WebSeedView> WebSeeds { get; init; } = Array.Empty<WebSeedView>();

    #endregion
}

#region Nested DTOs

/// <summary>Tracker information snapshot.</summary>
public sealed record TrackerInfoView
{
    public string Url { get; init; } = "";
    public int Tier { get; init; }
    public string Status { get; init; } = "";
    public int Peers { get; init; }
    public int Seeds { get; init; }
    public int Leeches { get; init; }
    public string ResponseTime { get; init; } = "-";
}

/// <summary>Connected peer snapshot.</summary>
public sealed record PeerView
{
    public string IpAddress { get; init; } = "";
    public int Port { get; init; }
    public string Client { get; init; } = "";
    public double DownloadRate { get; init; }
    public double UploadRate { get; init; }
    public string DownloadRateFormatted { get; init; } = "-";
    public string UploadRateFormatted { get; init; } = "-";
    public long Downloaded { get; init; }
    public long Uploaded { get; init; }
    public double Progress { get; init; }
    public string Flags { get; init; } = "";
    public double RoundTripTimeMs { get; init; }
}

/// <summary>File entry snapshot.</summary>
public sealed record FileView
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public double Progress { get; init; }
    public int Priority { get; init; }
    public float Availability { get; init; }
}

/// <summary>Web seed (HTTP source) snapshot.</summary>
public sealed record WebSeedView
{
    public string Url { get; init; } = "";
    public string Type { get; init; } = "";       // "BEP 19" or "BEP 17"
    public string Status { get; init; } = "";
    public double DownloadRate { get; init; }
    public string DownloadRateFormatted { get; init; } = "-";
    public long Downloaded { get; init; }
}

#endregion
