using System;

namespace vTorrent.Abstractions.Records;

/// <summary>
/// Database record for a torrent - matches SQLite schema
/// </summary>
public class TorrentRecord
{
    // Identity
    public string InfoHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public string? CreatedBy { get; set; }

    // Torrent metadata
    public long TotalSize { get; set; }
    public int PieceCount { get; set; }
    public int PieceSize { get; set; }
    public int FileCount { get; set; } = 1;
    public bool IsPrivate { get; set; }

    // Storage
    public string SavePath { get; set; } = string.Empty;
    public string? TorrentFilePath { get; set; }

    public string? ErrorMessage { get; set; }
    public double Progress { get; set; }
    public bool IsFinished { get; set; }
    public bool IsSeed { get; set; }

    // Orthogonal state dimensions
    public string? TransferPhase { get; set; }
    public string? FileOperation { get; set; }
    public string UserIntent { get; set; } = "Paused";
    public string? Health { get; set; }

    // Persistent statistics
    public long TotalUploaded { get; set; }
    public long TotalDownloaded { get; set; }
    public long TotalPayloadUploaded { get; set; }
    public long TotalPayloadDownloaded { get; set; }
    public long TotalFailedBytes { get; set; }
    public long TotalRedundantBytes { get; set; }

    // Time tracking (seconds)
    public long ActiveSeconds { get; set; }
    public long SeedingSeconds { get; set; }
    public long FinishedSeconds { get; set; }

    // Timestamps (Unix epoch)
    public long AddedAt { get; set; }
    public long? StartedAt { get; set; }
    public long? CompletedAt { get; set; }
    public long? LastSeenComplete { get; set; }
    public long? LastUpload { get; set; }
    public long? LastDownload { get; set; }
    public long? LastActiveAt { get; set; }

    // Per-torrent settings
    public int MaxConnections { get; set; } = -1;
    public int MaxUploads { get; set; } = -1;
    public int DownloadLimit { get; set; } = -1;
    public int UploadLimit { get; set; } = -1;
    public bool SequentialDownload { get; set; }
    public bool FirstLastPiecePriority { get; set; }
    public bool AutoManaged { get; set; } = true;
    public string? FilePriorities { get; set; } // JSON array of ints

    // Queue
    public int QueuePosition { get; set; }

    // Category (nullable - torrent may not be in a category)
    public int? CategoryId { get; set; }

    // Magnet link support
    public bool IsMagnetLink { get; set; }
    public string? MagnetUri { get; set; }

    // Metadata timestamps
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}
