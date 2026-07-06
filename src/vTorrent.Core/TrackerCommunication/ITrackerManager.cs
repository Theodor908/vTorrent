using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Core.TrackerCommunication.Models;

namespace vTorrent.Core.TrackerCommunication;

public interface ITrackerManager : IDisposable
{
    byte[] InfoHash { get; }
    byte[] PeerId { get; }
    IReadOnlyList<string> TrackerUrls { get; }
    IReadOnlyList<ITrackerClient> ActiveTrackers { get; }
    int TotalPeersDiscovered { get; }
    int TotalSeeders { get; }
    int TotalLeechers { get; }
    DateTime? LastSuccessfulAnnounce { get; }
    int NextAnnounceInterval { get; }

    /// <summary>
    /// Calculated time when the next announce will occur.
    /// Returns null if no successful announce has been made yet.
    /// </summary>
    DateTime? NextAnnounceTime { get; }

    /// <summary>
    /// Time remaining until the next tracker announce.
    /// Returns null if no successful announce has been made yet.
    /// Returns TimeSpan.Zero if announce is overdue.
    /// </summary>
    TimeSpan? TimeToNextAnnounce { get; }

    bool IsRunning { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();

    /// <summary>Stops periodic announcing/scraping without disposing clients (pause-safe).</summary>
    void PauseAnnouncing();

    /// <summary>Re-enables periodic announcing/scraping after PauseAnnouncing.</summary>
    void ResumeAnnouncing();

    Task<TrackerAnnounceResult> AnnounceAsync(TrackerRequest request, CancellationToken cancellationToken = default);
    Task<TrackerAnnounceResult> AnnounceStartedAsync(long left, CancellationToken cancellationToken = default);

    Task<TrackerAnnounceResult> AnnounceStoppedAsync(long uploaded, long downloaded, long left,
        CancellationToken cancellationToken = default);

    Task<TrackerAnnounceResult> AnnounceCompletedAsync(long uploaded, long downloaded,
        CancellationToken cancellationToken = default);

    Task<TrackerAnnounceResult> AnnounceRegularAsync(long uploaded, long downloaded, long left,
        CancellationToken cancellationToken = default);

    Task<TrackerScrapeResult> ScrapeAsync(CancellationToken cancellationToken = default);

    bool AddTracker(string trackerUrl);
    bool RemoveTracker(string trackerUrl);

    /// <summary>
    /// Force an immediate regular announce to all trackers, bypassing the scheduled timer.
    /// Analogous to libtorrent's force_reannounce().
    /// </summary>
    Task ForceReannounceAsync(CancellationToken cancellationToken = default);

    TrackerStatistics GetTrackerStatistics(string trackerUrl);

    IReadOnlyDictionary<string, TrackerStatistics> GetAllTrackerStatistics();

    event EventHandler<PeersDiscoveredEventArgs> PeersDiscovered;
    event EventHandler<AnnounceCompletedEventArgs> AnnounceCompleted;
    event EventHandler<TrackerFailedEventArgs> TrackerFailed;
    event EventHandler<ScrapeCompletedEventArgs> ScrapeCompleted;
}