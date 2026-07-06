using System.Collections.Generic;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// Result of removing a torrent and optionally deleting its files.
/// Platform-agnostic equivalent of Core's DeleteTorrentFilesResult.
/// </summary>
public sealed record DeleteResult
{
    /// <summary>
    /// Whether there are extra files in the torrent directory that were not part of the torrent.
    /// </summary>
    public bool HasExtraFiles { get; init; }

    /// <summary>
    /// Paths of extra files found in the torrent directory.
    /// </summary>
    public IReadOnlyList<string> ExtraFiles { get; init; } = [];

    /// <summary>
    /// The torrent's content directory (if single-folder torrent).
    /// </summary>
    public string? TorrentDirectory { get; init; }

    /// <summary>
    /// The torrent's save path.
    /// </summary>
    public string? SavePath { get; init; }
}
