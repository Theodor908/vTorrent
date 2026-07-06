namespace vTorrent.Abstractions.Enums;

/// <summary>
/// What phase of the transfer lifecycle is the torrent in?
/// Orthogonal to FileOperation, UserIntent, and TorrentHealth.
/// </summary>
public enum TransferPhase
{
    Idle,                  // Engine not running
    Stopping,              // Graceful shutdown: tracker stop-announce, disk flush
    Allocating,            // Creating file structure on disk
    CheckingResumeData,    // Validating fast-resume data against files
    CheckingFiles,         // Full piece hash verification
    FetchingMetadata,      // Downloading metadata from peers (magnet links)
    Connecting,            // Announcing to trackers, finding peers
    Downloading,           // Actively receiving pieces
    Seeding                // Complete — uploading to peers
}
