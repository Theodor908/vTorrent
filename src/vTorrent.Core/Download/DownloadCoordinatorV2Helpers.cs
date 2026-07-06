using vTorrent.Bencode.Torrents;

namespace vTorrent.Core.Download;

/// <summary>
/// BEP 52 helpers for DownloadCoordinator hash gate logic.
/// </summary>
public static class DownloadCoordinatorV2Helpers
{
    /// <summary>
    /// Returns true if the torrent requires hash gate (v2 or hybrid).
    /// V1 torrents have all piece hashes upfront — no gate needed.
    /// </summary>
    public static bool RequiresHashGate(TorrentInfo info)
    {
        return info.Version is TorrentVersion.V2 or TorrentVersion.Hybrid;
    }
}
