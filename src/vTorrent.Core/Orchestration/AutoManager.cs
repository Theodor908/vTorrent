using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Session;
using vTorrent.Abstractions.Enums;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Automatically manages which torrents are active based on configured limits.
/// Runs periodically and can be triggered manually.
/// Similar to libtorrent's auto_manage_torrents().
/// </summary>
public class AutoManager : IDisposable
{
    private readonly ILogger<AutoManager> _logger;
    private readonly StateIndex _stateIndex;
    private readonly QueueManager _queue;
    private readonly Func<ManagedTorrent, bool> _startTorrent;
    private readonly Func<ManagedTorrent, bool> _pauseTorrent;
    private readonly IOptionsMonitor<QueueSettings>? _queueMonitor;
    private readonly DateTime _sessionStartTime = DateTime.UtcNow;

    private Timer? _timer;
    private bool _isRunning;
    private bool _needsRecalculation;
    private readonly object _lock = new();

    #region Settings

    /// <summary>
    /// Maximum number of active downloads (-1 = unlimited)
    /// </summary>
    public int MaxActiveDownloads { get; set; } = 5;

    /// <summary>
    /// Maximum number of active seeds (-1 = unlimited)
    /// </summary>
    public int MaxActiveSeeds { get; set; } = -1;

    /// <summary>
    /// Maximum total active torrents (-1 = unlimited)
    /// </summary>
    public int MaxActiveTorrents { get; set; } = 10;

    /// <summary>
    /// When true, inactive (slow) torrents bypass per-type slot limits.
    /// libtorrent: dont_count_slow_torrents
    /// </summary>
    public bool DontCountSlowTorrents { get; set; } = true;

    /// <summary>
    /// Download rate threshold below which torrent is considered inactive (bytes/s)
    /// </summary>
    public int InactiveDownRate { get; set; } = 2048;

    /// <summary>
    /// Upload rate threshold below which torrent is considered inactive (bytes/s)
    /// </summary>
    public int InactiveUpRate { get; set; } = 2048;

    /// <summary>
    /// Grace period after torrent starts before it can be considered inactive (seconds)
    /// </summary>
    public int InactiveGracePeriodSeconds { get; set; } = 60;

    /// <summary>
    /// Interval between auto-management cycles (default: 30 seconds)
    /// </summary>
    public TimeSpan RecalculateInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether auto-management is enabled
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    #endregion

    #region Events

    /// <summary>
    /// Raised when a torrent is auto-started
    /// </summary>
    public event EventHandler<AutoManagerEventArgs>? TorrentAutoStarted;

    /// <summary>
    /// Raised when a torrent is auto-paused
    /// </summary>
    public event EventHandler<AutoManagerEventArgs>? TorrentAutoPaused;

    /// <summary>
    /// Raised after recalculation completes
    /// </summary>
    public event EventHandler<AutoManagerRecalculatedEventArgs>? Recalculated;

    #endregion

    public AutoManager(
        StateIndex stateIndex,
        QueueManager queue,
        Func<ManagedTorrent, bool> startTorrent,
        Func<ManagedTorrent, bool> pauseTorrent,
        ILogger<AutoManager> logger,
        IOptionsMonitor<QueueSettings>? queueMonitor = null)
    {
        _stateIndex = stateIndex ?? throw new ArgumentNullException(nameof(stateIndex));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _startTorrent = startTorrent ?? throw new ArgumentNullException(nameof(startTorrent));
        _pauseTorrent = pauseTorrent ?? throw new ArgumentNullException(nameof(pauseTorrent));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _queueMonitor = queueMonitor;
    }

    #region Lifecycle

    /// <summary>
    /// Start auto-management timer
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_isRunning)
                return;

            _isRunning = true;

            // Schedule first tick at the startup grace expiry (not T=0 which will just be rejected).
            // Subsequent ticks at the normal recalculation interval.
            var startupGrace = _queueMonitor?.CurrentValue.AutoManageStartup ?? 5;
            var firstTick = TimeSpan.FromSeconds(Math.Max(1, startupGrace));
            _timer = new Timer(OnTimerTick, null, firstTick, RecalculateInterval);
            _logger.LogInformation("Auto-manager started (first tick: {First}s, interval: {Interval}s)",
                firstTick.TotalSeconds, RecalculateInterval.TotalSeconds);
        }
    }

    /// <summary>
    /// Stop auto-management timer
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _timer?.Dispose();
            _timer = null;
            _logger.LogInformation("Auto-manager stopped");
        }
    }

    /// <summary>
    /// Trigger immediate recalculation
    /// </summary>
    public void Trigger()
    {
        lock (_lock)
        {
            _needsRecalculation = true;
            Recalculate(); // Always run immediately (libtorrent: trigger_auto_manage)
        }
    }

    public void Dispose()
    {
        Stop();
    }

    #endregion

    #region Recalculation

    private void OnTimerTick(object? state)
    {
        if (!IsEnabled)
            return;

        lock (_lock)
        {
            // Always recalculate on timer, or if triggered
            Recalculate();
            _needsRecalculation = false;
        }
    }

    /// <summary>
    /// Main recalculation logic
    /// </summary>
    private void Recalculate()
    {
        if (_queueMonitor != null)
        {
            var queue = _queueMonitor.CurrentValue;
            RecalculateInterval = TimeSpan.FromSeconds(queue.AutoManageInterval);
        }

        if (_queueMonitor != null)
        {
            var startupGrace = _queueMonitor.CurrentValue.AutoManageStartup;
            if ((DateTime.UtcNow - _sessionStartTime).TotalSeconds < startupGrace)
            {
                _logger.LogTrace("Auto-manage: within startup grace period ({Grace}s), skipping", startupGrace);
                return;
            }
        }

        try
        {
            int started = 0;
            int paused = 0;

            // Get current counts
            int activeDownloads = _stateIndex.DownloadingCount;
            int activeSeeds = _stateIndex.SeedingCount;
            int totalActive = activeDownloads + activeSeeds;

            _logger.LogTrace("Auto-manage: downloads={Downloads}, seeds={Seeds}, total={Total}",
                activeDownloads, activeSeeds, totalActive);

            // Slow torrent detection: inactive torrents don't count against slot limits
            int inactiveDownloads = 0;
            int inactiveSeeds = 0;

            if (DontCountSlowTorrents)
            {
                // Intent-gated sets: paused torrents keep their phase (orthogonal
                // model) and must not be subtracted from the active-slot count.
                foreach (var torrent in _stateIndex.ActiveDownloading)
                {
                    if (IsInactive(torrent))
                        inactiveDownloads++;
                }
                foreach (var torrent in _stateIndex.ActiveSeeding)
                {
                    if (IsInactive(torrent))
                        inactiveSeeds++;
                }
            }

            int adjustedActiveDownloads = activeDownloads - inactiveDownloads;
            int adjustedActiveSeeds = activeSeeds - inactiveSeeds;

            // Calculate available slots
            int downloadSlots = CalculateAvailableSlots(adjustedActiveDownloads, MaxActiveDownloads, totalActive, MaxActiveTorrents);
            int seedSlots = CalculateAvailableSlots(adjustedActiveSeeds, MaxActiveSeeds, totalActive - downloadSlots, MaxActiveTorrents);

            // Start queued downloads if slots available
            if (downloadSlots > 0)
            {
                var candidates = _queue.GetQueuedDownloadCandidates();
                started += StartCandidates(candidates, downloadSlots, ref totalActive);
            }

            // Start queued seeds if slots available
            if (seedSlots > 0)
            {
                var candidates = _queue.GetQueuedSeedCandidates();
                started += StartCandidates(candidates, seedSlots, ref totalActive);
            }

            // If over limits, pause excess torrents (lowest priority first)
            if (MaxActiveTorrents > 0 && totalActive > MaxActiveTorrents)
            {
                paused += PauseExcessTorrents(totalActive - MaxActiveTorrents);
            }

            if (started > 0 || paused > 0)
            {
                _logger.LogDebug("Auto-manage: started {Started}, paused {Paused}", started, paused);
            }

            Recalculated?.Invoke(this, new AutoManagerRecalculatedEventArgs(started, paused));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-management recalculation failed");
        }
    }

    private int CalculateAvailableSlots(int current, int limit, int totalActive, int totalLimit)
    {
        if (limit < 0 && totalLimit < 0)
            return int.MaxValue; // Unlimited

        int available = int.MaxValue;

        if (limit >= 0)
            available = Math.Min(available, limit - current);

        if (totalLimit >= 0)
            available = Math.Min(available, totalLimit - totalActive);

        return Math.Max(0, available);
    }

    private int StartCandidates(IReadOnlyList<ManagedTorrent> candidates, int maxToStart, ref int totalActive)
    {
        int started = 0;

        foreach (var torrent in candidates)
        {
            if (started >= maxToStart)
                break;

            if (!torrent.IsAutoManaged || torrent.UserPaused)
                continue;

            // Check total limit
            if (MaxActiveTorrents > 0 && totalActive >= MaxActiveTorrents)
                break;

            if (_startTorrent(torrent))
            {
                started++;
                totalActive++;
                _logger.LogDebug("Auto-started: {Name}", torrent.Name);
                TorrentAutoStarted?.Invoke(this, new AutoManagerEventArgs(torrent.InfoHash, torrent.Name));
            }
        }

        return started;
    }

    private int PauseExcessTorrents(int count)
    {
        int paused = 0;

        // Intent-gated sets: only actually-running torrents are pause candidates.
        // Raw phase sets now include paused torrents (orthogonal model), which are
        // unpausable here and would shadow the real over-limit actives.
        var activeTorrents = new List<ManagedTorrent>();
        activeTorrents.AddRange(_stateIndex.ActiveDownloading);
        activeTorrents.AddRange(_stateIndex.ActiveSeeding);

        // LIBTORRENT-STYLE PARTIAL SORT OPTIMIZATION:
        // Instead of sorting all active torrents, only find the top 'count' torrents to pause.
        // This is O(n*k) instead of O(n*log(n)) when k << n.
        // For typical usage (< 100 torrents) the difference is minimal,
        // but for large collections this is significant.
        var toPause = PartialSortTopN(
            activeTorrents,
            count,
            (a, b) => b.QueuePosition.CompareTo(a.QueuePosition), // Higher position = lower priority = pause first
            t => t.IsAutoManaged); // Only consider auto-managed torrents

        foreach (var torrent in toPause)
        {
            if (_pauseTorrent(torrent))
            {
                paused++;
                _logger.LogDebug("Auto-paused: {Name}", torrent.Name);
                TorrentAutoPaused?.Invoke(this, new AutoManagerEventArgs(torrent.InfoHash, torrent.Name));
            }
        }

        return paused;
    }

    /// <summary>
    /// Determines if a torrent is considered inactive (slow) per libtorrent's pattern.
    /// </summary>
    private bool IsInactive(ManagedTorrent torrent)
    {
        if (!DontCountSlowTorrents)
            return false;

        var stats = torrent.Statistics;
        if (stats == null)
            return false;

        // Grace period: newly started torrents are never considered inactive
        if (stats.ActiveDuration.TotalSeconds < InactiveGracePeriodSeconds)
            return false;

        bool isDownloading = torrent.GetStatus().Phase == TransferPhase.Downloading;
        if (isDownloading)
            return stats.PayloadDownloadRate < InactiveDownRate;
        else
            return stats.PayloadUploadRate < InactiveUpRate;
    }

    /// <summary>
    /// Partial sort: efficiently find top N elements matching a filter.
    /// Uses selection algorithm (O(n*k)) instead of full sort (O(n*log(n))).
    /// Based on libtorrent's partial_sort optimization in auto_manage_torrents.
    /// </summary>
    private static List<T> PartialSortTopN<T>(
        IReadOnlyList<T> items,
        int n,
        Comparison<T> comparison,
        Func<T, bool>? filter = null)
    {
        // For small collections or large n, just use LINQ (which has its own optimizations)
        if (items.Count <= 50 || n >= items.Count / 2)
        {
            var query = filter != null
                ? items.Where(filter).OrderBy(x => x, Comparer<T>.Create((a, b) => -comparison(a, b)))
                : items.OrderBy(x => x, Comparer<T>.Create((a, b) => -comparison(a, b)));
            return query.Take(n).ToList();
        }

        // For larger collections with small n, use a min-heap approach (O(n*log(k)))
        // This keeps only the top k elements in memory
        var result = new SortedSet<(T item, int index)>(
            Comparer<(T item, int index)>.Create((a, b) =>
            {
                int cmp = comparison(a.item, b.item);
                return cmp != 0 ? cmp : a.index.CompareTo(b.index); // Stable sort
            }));

        int idx = 0;
        foreach (var item in items)
        {
            if (filter != null && !filter(item))
            {
                idx++;
                continue;
            }

            if (result.Count < n)
            {
                result.Add((item, idx));
            }
            else if (comparison(item, result.Min.item) > 0)
            {
                // Current item is better than the worst in our set
                result.Remove(result.Min);
                result.Add((item, idx));
            }
            idx++;
        }

        return result.Select(x => x.item).ToList();
    }

    #endregion

    #region Settings Update

    /// <summary>
    /// Update limits from settings
    /// </summary>
    public void UpdateLimits(int maxDownloads, int maxSeeds, int maxTotal)
    {
        MaxActiveDownloads = maxDownloads;
        MaxActiveSeeds = maxSeeds;
        MaxActiveTorrents = maxTotal;

        _logger.LogDebug("Auto-manager limits updated: downloads={Downloads}, seeds={Seeds}, total={Total}",
            maxDownloads, maxSeeds, maxTotal);

        // Trigger recalculation with new limits
        Trigger();
    }

    #endregion
}

/// <summary>
/// Event args for auto-manager torrent actions
/// </summary>
public class AutoManagerEventArgs : EventArgs
{
    public string InfoHash { get; }
    public string Name { get; }

    public AutoManagerEventArgs(string infoHash, string name)
    {
        InfoHash = infoHash;
        Name = name;
    }
}

/// <summary>
/// Event args for recalculation completion
/// </summary>
public class AutoManagerRecalculatedEventArgs : EventArgs
{
    public int TorrentsStarted { get; }
    public int TorrentsPaused { get; }

    public AutoManagerRecalculatedEventArgs(int started, int paused)
    {
        TorrentsStarted = started;
        TorrentsPaused = paused;
    }
}
