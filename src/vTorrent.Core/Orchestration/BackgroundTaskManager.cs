using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Merkle;
using vTorrent.Core.Persistence;
using vTorrent.Core.Session;
using vTorrent.Storage;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Records;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Manages background services: auto-save, stats update, stats history, and seeding limits.
/// Extracted from TorrentOrchestrator as part of god class decomposition (Phase 5, Task 5.3).
/// </summary>
internal class BackgroundTaskManager
{
    private readonly TorrentOrchestrator _orch;
    private readonly ILogger<BackgroundTaskManager> _logger;
    private readonly SeedingLimitEnforcer _seedingLimitEnforcer;
    private readonly ConcurrentQueue<(string InfoHash, SeedingLimitResult Result)> _seedingLimitQueue = new();
    private readonly MerkleTreeStore? _treeStore;

    // Timers
    private Timer? _autoSaveTimer;
    private Timer? _statsUpdateTimer;
    private Timer? _statsHistoryTimer;

    public BackgroundTaskManager(
        TorrentOrchestrator orchestrator,
        ILoggerFactory loggerFactory,
        SeedingLimitEnforcer seedingLimitEnforcer)
    {
        _orch = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _logger = loggerFactory.CreateLogger<BackgroundTaskManager>();
        _seedingLimitEnforcer = seedingLimitEnforcer;

        var resumeDir = _orch.Persistence.ResumeDirectory;
        if (!string.IsNullOrEmpty(resumeDir))
            _treeStore = new MerkleTreeStore(resumeDir);
    }

    internal void Start()
    {
        var autoSaveSettings = _orch.AutoSaveMonitorInternal?.CurrentValue ?? _orch.Persistence.Settings.AutoSave;
        var autoSaveInterval = TimeSpan.FromMinutes(autoSaveSettings.IntervalMinutes);
        _autoSaveTimer = new Timer(OnAutoSaveTimer, null, autoSaveInterval, autoSaveInterval);

        _statsUpdateTimer = new Timer(OnStatsUpdateTimer, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        _statsHistoryTimer = new Timer(OnStatsHistoryTimer, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        // Start bandwidth coordinator for rate limiting
        _orch.BandwidthCoordinator.Start();

        _logger.LogDebug("Background services started");
    }

    internal void Stop()
    {
        _autoSaveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _autoSaveTimer?.Dispose();
        _autoSaveTimer = null;

        _statsUpdateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _statsUpdateTimer?.Dispose();
        _statsUpdateTimer = null;

        _statsHistoryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _statsHistoryTimer?.Dispose();
        _statsHistoryTimer = null;

        // Stop bandwidth coordinator
        _orch.BandwidthCoordinator.Stop();

        _logger.LogDebug("Background services stopped");
    }

    /// <summary>
    /// Updates the auto-save timer interval based on settings.
    /// </summary>
    internal void UpdateAutoSaveTimer(GlobalSettings settings)
    {
        if (_autoSaveTimer == null) return;

        if (settings.AutoSave.Enabled)
        {
            var interval = TimeSpan.FromMinutes(settings.AutoSave.IntervalMinutes);
            _autoSaveTimer.Change(interval, interval);
            _logger.LogDebug("Auto-save timer updated to {Interval} minutes", settings.AutoSave.IntervalMinutes);
        }
        else
        {
            _autoSaveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _logger.LogDebug("Auto-save timer disabled");
        }
    }

    private void OnAutoSaveTimer(object? state)
    {
        _ = OnAutoSaveTimerAsync();
    }

    private async Task OnAutoSaveTimerAsync()
    {
        if (_orch.IsShuttingDown)
            return;

        _logger.LogDebug("Auto-save triggered");

        try
        {
            var activeTorrents = _orch.StateIndex.GetActiveTorrents().ToList();
            int savedCount = 0;

            // Save resume data only for torrents that have changes
            foreach (var torrent in activeTorrents)
            {
                // Only save if changes occurred since last save
                if (!torrent.Statistics.NeedSaveResume)
                {
                    continue;
                }

                _orch.UpdateResumeDataFromTorrent(torrent);
                await _orch.Persistence.SaveResumeDataAsync(torrent.InfoHash, torrent.ResumeData).ConfigureAwait(false);

                // Save stats including progress and completion flags to database
                await _orch.Persistence.UpdateTorrentStatsAsync(torrent.InfoHash, new TorrentStatsUpdate(
                    totalUploaded: torrent.Statistics.AllTimeUploaded,
                    totalDownloaded: torrent.Statistics.AllTimeDownloaded,
                    progress: torrent.Progress,
                    activeSeconds: (long)torrent.Statistics.ActiveDuration.TotalSeconds,
                    seedingSeconds: (long)torrent.Statistics.SeedingDuration.TotalSeconds,
                    isFinished: torrent.IsFinished,
                    isSeed: torrent.IsSeed,
                    totalPayloadUploaded: torrent.Statistics.AllTimePayloadUploaded,
                    totalPayloadDownloaded: torrent.Statistics.AllTimePayloadDownloaded)).ConfigureAwait(false);

                // Clear the dirty flag after successful save
                torrent.Statistics.NeedSaveResume = false;
                savedCount++;
            }

            // Save session state
            _orch.SaveSessionStateIfNeeded();

            // Save queue positions periodically to prevent data loss on crash
            var queueUpdates = _orch.QueueManager.GetQueuePositionUpdates();
            if (queueUpdates.Count > 0)
            {
                await _orch.Persistence.BatchUpdateQueuePositionsAsync(queueUpdates).ConfigureAwait(false);
            }

            // Periodic peer cache save for crash resilience (5-min effective via auto-save interval)
            foreach (var torrent in activeTorrents)
            {
                if (torrent.Engine != null)
                {
                    try
                    {
                        await torrent.Engine.SavePeerCacheAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to save peer cache for {InfoHash}", torrent.InfoHash);
                    }
                }
            }

            // BEP 52: Save merkle trees for v2/hybrid torrents (canonical file order)
            if (_treeStore != null)
            {
                foreach (var torrent in activeTorrents)
                {
                    var orderedTrees = GetTreesInCanonicalOrder(torrent);
                    if (orderedTrees != null)
                    {
                        try
                        {
                            await _treeStore.SaveAsync(
                                torrent.InfoHash, orderedTrees).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to save merkle trees for {InfoHash}",
                                torrent.InfoHash);
                        }
                    }
                }
            }

            // Cleanup old statistics periodically
            await _orch.Persistence.CleanupOldStatisticsAsync(7).ConfigureAwait(false);

            if (savedCount > 0)
            {
                _logger.LogDebug("Auto-save completed: {SavedCount}/{TotalCount} torrents had changes",
                    savedCount, activeTorrents.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-save failed");
        }
    }

    /// <summary>
    /// Get merkle trees in canonical file order (matching TorrentInfo.Files order).
    /// MerkleTreeStore.LoadAsync validates roots positionally — Dictionary enumeration order is NOT safe.
    /// </summary>
    private static MerkleTree[]? GetTreesInCanonicalOrder(ManagedTorrent torrent)
    {
        if (torrent.MerkleTrees == null || torrent.MerkleTrees.Count == 0 || torrent.Torrent == null)
            return null;

        var files = torrent.Torrent.Info.Files
            ?? FileTreeParser.Flatten(torrent.Torrent.Info.FileTreeV2!);

        var ordered = new List<MerkleTree>();
        foreach (var file in files)
        {
            if (file.PiecesRoot.HasValue && torrent.MerkleTrees.TryGetValue(file.PiecesRoot.Value, out var tree))
                ordered.Add(tree);
        }

        return ordered.Count > 0 ? ordered.ToArray() : null;
    }

    private void OnStatsUpdateTimer(object? state)
    {
        if (_orch.IsShuttingDown)
            return;

        try
        {
            var snapshot = _orch.StateIndex.GetSnapshot();
            var torrentList = _orch.TorrentsInternal.ToList();
            var statistics = _orch.Statistics;

            statistics.DownloadingTorrents = snapshot.Downloading;
            statistics.SeedingTorrents = snapshot.Seeding;
            statistics.PausedTorrents = snapshot.Paused;
            statistics.CheckingTorrents = snapshot.Checking;
            statistics.ErrorTorrents = snapshot.Error;

            statistics.GlobalDownloadRate = torrentList.Sum(t => t.DownloadRate);
            statistics.GlobalUploadRate = torrentList.Sum(t => t.UploadRate);
            statistics.TotalPeersConnected = torrentList.Sum(t => t.ConnectedPeers);
            statistics.TotalConnectedSeeds = torrentList.Sum(t => t.ConnectedSeeds);

            // Accumulate duration timers (called every 1 second)
            // NOTE: We sync stats from engine FIRST, then aggregate AFTER to avoid stale data
            var oneSecond = TimeSpan.FromSeconds(1);
            foreach (var torrent in torrentList)
            {
                var torrentStatus = torrent.GetStatus();

                // Orthogonal state model: paused torrents keep Phase=Downloading/Seeding,
                // so duration accrual and seeding limits must also require Intent=Active.
                var isRunning = torrentStatus.Intent == UserIntent.Active;

                // Active torrents (not paused, not stopped)
                if (isRunning && torrentStatus.Phase is TransferPhase.Downloading or
                    TransferPhase.Seeding or TransferPhase.Connecting)
                {
                    torrent.Statistics.ActiveDuration += oneSecond;
                }

                // Seeding torrents (100% complete and uploading)
                if (isRunning && torrentStatus.Phase == TransferPhase.Seeding)
                {
                    torrent.Statistics.SeedingDuration += oneSecond;
                    torrent.Statistics.FinishedDuration += oneSecond;

                    // Check seeding limits (ratio and time)
                    var limitResult = _seedingLimitEnforcer.CheckLimits(torrent);
                    if (limitResult.LimitReached)
                    {
                        // Queue for processing after the loop (can't await in timer callback)
                        _seedingLimitQueue.Enqueue((torrent.InfoHash, limitResult));
                    }
                }

                // Sync stats from engine via single snapshot (replaces 20+ individual property reads)
                if (torrent.Engine != null)
                {
                    var status = torrent.Engine.GetStatus();

                    // Sync rates (payload for UI display)
                    torrent.Statistics.DownloadRate = status.PayloadDownloadRate;
                    torrent.Statistics.UploadRate = status.PayloadUploadRate;
                    torrent.Statistics.PayloadDownloadRate = status.PayloadDownloadRate;
                    torrent.Statistics.PayloadUploadRate = status.PayloadUploadRate;
                    torrent.Statistics.SmoothedPayloadDownloadRate = status.SmoothedPayloadDownloadRate;

                    // Sync byte counters
                    torrent.Statistics.SessionDownloaded = status.SessionDownloaded;
                    torrent.Statistics.SessionUploaded = status.SessionUploaded;
                    torrent.Statistics.SessionPayloadDownloaded = status.SessionPayloadDownloaded;
                    torrent.Statistics.SessionPayloadUploaded = status.SessionPayloadUploaded;
                    torrent.Statistics.SessionVerifiedDownloaded = status.VerifiedDownloaded;

                    // Sync progress (use wanted bytes for selective download support)
                    torrent.Statistics.TotalDone = (long)(status.VerifiedProgress * status.TotalSize);
                    torrent.Statistics.TotalWanted = status.TotalWanted;
                    torrent.Statistics.TotalWantedDone = status.TotalWantedDone;
                    torrent.Statistics.PiecesCompleted = status.PiecesCompleted;

                    // Sync peers
                    torrent.Statistics.ConnectedPeers = status.ConnectedPeers;
                    torrent.Statistics.ConnectedSeeds = status.ConnectedSeeds;

                    // DHT peer count update
                    _orch.DhtCoordinator.UpdateDhtPeerCount(torrent.InfoHash, status.ConnectedPeers);

                    // Sync tracker
                    torrent.Statistics.LastAnnounce = status.LastAnnounce;
                    torrent.Statistics.AnnounceInterval = status.AnnounceInterval;
                    torrent.Statistics.ReannounceIn = status.TimeToNextAnnounce;

                    // Sync availability (UpdateFileAvailability must still be called on the engine)
                    torrent.Engine.UpdateFileAvailability();
                    torrent.Statistics.Availability = torrent.Engine.Availability;

                    // Sync endgame
                    torrent.Statistics.IsEndgame = status.IsEndgame;
                    torrent.Statistics.EndgameWastedBytes = status.EndgameWastedBytes;
                    torrent.Statistics.EndgameDuplicateBlocks = status.EndgameDuplicateBlocks;
                    torrent.Statistics.FailedBytes = status.FailedBytes;
                }
            }

            // Aggregate session-wide totals (kept for SessionOverviewViewModel)
            statistics.TotalBytesReceived = torrentList.Sum(t => t.Statistics.SessionDownloaded);
            statistics.TotalBytesSent = torrentList.Sum(t => t.Statistics.SessionUploaded);

            // BEP 24: Set external IP from voter consensus
            statistics.ExternalIpAddress = _orch.ExternalIpVoter.GetConsensusIp()?.ToString();

            _orch.RaiseStatisticsUpdated(statistics.CreateSnapshot());

            // Update DHT node count if DHT is running (periodic refresh for UI)
            _orch.DhtCoordinator.BroadcastState();

            // Process seeding limit actions (async, fire-and-forget)
            ProcessSeedingLimitQueue();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Stats update error");
        }
    }

    /// <summary>
    /// Process queued seeding limit actions.
    /// </summary>
    private void ProcessSeedingLimitQueue()
    {
        _ = ProcessSeedingLimitQueueAsync();
    }

    private async Task ProcessSeedingLimitQueueAsync()
    {
        while (_seedingLimitQueue.TryDequeue(out var item))
        {
            try
            {
                await HandleSeedingLimitReachedAsync(item.InfoHash, item.Result).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling seeding limit for {InfoHash}", item.InfoHash);
            }
        }
    }

    /// <summary>
    /// Handle a torrent that has reached its seeding limit.
    /// </summary>
    private async Task HandleSeedingLimitReachedAsync(string infoHash, SeedingLimitResult result)
    {
        _logger.LogInformation(
            "Processing seeding limit action for '{Name}': {LimitType} limit reached ({Current:F2} >= {Limit:F2}), action: {Action}",
            result.TorrentName, result.Type, result.CurrentValue, result.LimitValue, result.Action);

        switch (result.Action)
        {
            case SeedingLimitAction.Pause:
                await _orch.PauseTorrentAsync(infoHash).ConfigureAwait(false);
                _orch.RaiseSeedingLimitReached(infoHash, result);
                break;

            case SeedingLimitAction.Remove:
                await _orch.RemoveTorrentAsync(infoHash, deleteFiles: false).ConfigureAwait(false);
                _orch.RaiseSeedingLimitReached(infoHash, result);
                break;

            case SeedingLimitAction.None:
                // Just log, no action
                _logger.LogDebug("Seeding limit reached for {Name} but no action configured", result.TorrentName);
                break;
        }
    }

    private void OnStatsHistoryTimer(object? state)
    {
        _ = OnStatsHistoryTimerAsync();
    }

    private async Task OnStatsHistoryTimerAsync()
    {
        if (_orch.IsShuttingDown)
            return;

        try
        {
            var statistics = _orch.Statistics;

            // Record session-wide stats
            await _orch.Persistence.RecordStatisticsSnapshotAsync(new StatisticsSnapshot(
                null,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                statistics.GlobalDownloadRate,
                statistics.GlobalUploadRate,
                statistics.TotalBytesReceived,
                statistics.TotalBytesSent,
                statistics.TotalPeersConnected,
                statistics.SeedingTorrents)).ConfigureAwait(false);

            // Record per-torrent stats
            foreach (var torrent in _orch.StateIndex.GetActiveTorrents())
            {
                await _orch.Persistence.RecordStatisticsSnapshotAsync(new StatisticsSnapshot(
                    torrent.InfoHash,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    torrent.DownloadRate,
                    torrent.UploadRate,
                    torrent.Statistics.AllTimeDownloaded,
                    torrent.Statistics.AllTimeUploaded,
                    torrent.ConnectedPeers,
                    torrent.ConnectedSeeds)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record statistics history");
        }
    }
}
