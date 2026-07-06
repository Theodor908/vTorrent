using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Events;

/// <summary>
/// Batch stats update -- the primary UI refresh event.
/// Carries session statistics and (when available) the session overview DTO
/// plus the list of torrents that changed since last update
/// (libtorrent post_torrent_updates pattern).
/// </summary>
public class StatisticsUpdatedEventArgs : EventArgs
{
    /// <summary>
    /// Session statistics snapshot.
    /// </summary>
    public SessionStatistics Statistics { get; init; } = null!;

    /// <summary>
    /// New-style session overview DTO. Populated when available.
    /// </summary>
    public SessionOverview? Session { get; init; }

    /// <summary>
    /// List of torrents that changed since last update.
    /// </summary>
    public IReadOnlyList<TorrentSnapshot> UpdatedTorrents { get; init; } = Array.Empty<TorrentSnapshot>();

    public StatisticsUpdatedEventArgs() { }

    public StatisticsUpdatedEventArgs(SessionStatistics statistics)
    {
        Statistics = statistics;
    }
}
