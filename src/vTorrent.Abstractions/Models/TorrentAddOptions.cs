using vTorrent.Abstractions.Enums;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// Options for adding a new torrent, passed from UI dialogs to the engine.
/// </summary>
public class TorrentAddOptions
{
    public string? SavePath { get; init; }
    public bool StartImmediately { get; init; } = true;
    public bool SequentialDownload { get; init; }
    public bool FirstLastPiecePriority { get; init; }
    public bool AddToTopOfQueue { get; init; }

    /// <summary>
    /// Per-file priorities. Null = all Normal.
    /// Index = file index in torrent, Value = priority.
    /// </summary>
    public FilePriority[]? FilePriorities { get; init; }

    /// <summary>
    /// Seed mode: assume all pieces are present, verify lazily on upload.
    /// Set true when adding a torrent created from local files.
    /// libtorrent parity: torrent_flags::seed_mode.
    /// </summary>
    public bool SeedMode { get; init; }
}
