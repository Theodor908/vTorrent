namespace vTorrent.Abstractions.Enums;

/// <summary>
/// Is the torrent healthy?
/// </summary>
public enum TorrentHealth
{
    Ok,
    Stalled,               // No transfer activity for configurable threshold
    Error,                 // Engine error (disk, hash failure, etc.)
    MissingFiles           // Expected files not found on disk
}
