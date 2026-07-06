using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.TrackerCommunication.Models;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Interfaces;

namespace vTorrent.Core.TrackerCommunication;

public class TrackerManager : ITrackerManager
{
    private readonly ILogger<TrackerManager> _logger;
    private readonly TrackerClientFactory _trackerFactory;
    private readonly IOptionsMonitor<TrackerSettings> _trackerMonitor;

    private readonly ConcurrentDictionary<string, TrackerState> _trackers;
    private readonly ConcurrentDictionary<string, TrackerStatistics> _statistics;
    private readonly List<string> _trackerUrls;
    
    private readonly byte[] _infoHash;
    private readonly byte[] _peerId;
    private readonly bool _isPrivateTorrent;
    private readonly IExternalIpVoter? _externalIpVoter;
    private readonly bool _isI2pTorrent;
    private readonly bool _allowMixedMode;

    private bool _isRunning;
    private Timer? _announceTimer;
    private Timer? _scrapeTimer;
    // Set by PauseAnnouncing, cleared by AnnounceStartedAsync: prevents an in-flight
    // periodic announce from recreating the announce timer after pause disposed it
    // (Timer.Dispose does not abort an executing callback).
    private volatile bool _announcingPaused;
    private readonly SemaphoreSlim _announceLock = new(1, 1);
    private readonly CancellationTokenSource _stopCts = new();
    private bool _disposed;

    /// <summary>
    /// Redundant bytes accumulated since last announce.
    /// Set by the engine before announcing so the tracker can include or exclude them.
    /// </summary>
    public long RedundantBytes { get; set; }

    private int _totalPeersDiscovered;
    private int _totalSeeders;
    private int _totalLeechers;
    private DateTime? _lastSuccessfulAnnounce;
    private int _nextAnnounceInterval;
    private int _baseAnnounceInterval;
    private int _consecutiveAnnounceFailures;

    public event EventHandler<PeersDiscoveredEventArgs> PeersDiscovered;
    public event EventHandler<AnnounceCompletedEventArgs> AnnounceCompleted;
    public event EventHandler<TrackerFailedEventArgs> TrackerFailed;
    public event EventHandler<ScrapeCompletedEventArgs> ScrapeCompleted;
    
    public byte[] InfoHash => _infoHash;
    public byte[] PeerId => _peerId;
    public IReadOnlyList<string> TrackerUrls => _trackerUrls.AsReadOnly();
    public IReadOnlyList<ITrackerClient> ActiveTrackers => _trackers.Values
        .Where(t => t.Client.IsAvailable)
        .Select(t => t.Client)
        .ToList();
    public int TotalPeersDiscovered => _totalPeersDiscovered;
    public int TotalSeeders => _totalSeeders;
    public int TotalLeechers => _totalLeechers;
    public DateTime? LastSuccessfulAnnounce => _lastSuccessfulAnnounce;
    public int NextAnnounceInterval => _nextAnnounceInterval;

    /// <summary>
    /// Calculated time when the next announce will occur.
    /// </summary>
    public DateTime? NextAnnounceTime => _lastSuccessfulAnnounce.HasValue
        ? _lastSuccessfulAnnounce.Value.AddSeconds(_nextAnnounceInterval)
        : null;

    /// <summary>
    /// Time remaining until the next tracker announce.
    /// Returns TimeSpan.Zero if announce is overdue.
    /// </summary>
    public TimeSpan? TimeToNextAnnounce
    {
        get
        {
            if (!NextAnnounceTime.HasValue)
                return null;

            var remaining = NextAnnounceTime.Value - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public bool IsRunning => _isRunning;

    private readonly int _peerKey;
    
    public TrackerManager(
        byte[] infoHash,
        byte[] peerId,
        IEnumerable<string> trackerUrls,
        TrackerClientFactory trackerFactory,
        IOptionsMonitor<TrackerSettings> trackerMonitor,
        ILogger<TrackerManager> logger,
        int peerKey,
        bool isPrivateTorrent = false,
        IExternalIpVoter? externalIpVoter = null,
        bool isI2pTorrent = false,
        bool allowMixedMode = false)
    {
        if (infoHash == null || infoHash.Length != 20)
            throw new ArgumentException("InfoHash must be exactly 20 bytes");
        if (peerId == null || peerId.Length != 20)
            throw new ArgumentException("PeerId must be exactly 20 bytes");

        _infoHash = infoHash;
        _peerId = peerId;
        _trackerFactory = trackerFactory ?? throw new ArgumentNullException(nameof(trackerFactory));
        _trackerMonitor = trackerMonitor ?? throw new ArgumentNullException(nameof(trackerMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isPrivateTorrent = isPrivateTorrent;
        _externalIpVoter = externalIpVoter;

        _trackers = new ConcurrentDictionary<string, TrackerState>();
        _statistics = new ConcurrentDictionary<string, TrackerStatistics>();
        _trackerUrls = new List<string>();
        _nextAnnounceInterval = TrackerConstants.DefaultAnnounceInterval;
        _baseAnnounceInterval = TrackerConstants.DefaultAnnounceInterval;

        _peerKey = peerKey;

        _isI2pTorrent = isI2pTorrent;
        _allowMixedMode = allowMixedMode;

        // Initialize trackers
        InitializeTrackers(trackerUrls);

        _logger.LogDebug("TrackerManager created with {Count} trackers for InfoHash: {InfoHash}",
            _trackers.Count, Convert.ToHexString(_infoHash));
    }
    
    private void InitializeTrackers(IEnumerable<string> trackerUrls)
    {
        int tier = 0;
        foreach (var url in trackerUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
                continue;

            if (!_trackerFactory.IsSupported(url))
            {
                _logger.LogWarning("Tracker protocol not supported: {Url}", url);
                continue;
            }

            // Mixed mode enforcement: skip non-I2P trackers for pure I2P torrents
            if (_isI2pTorrent && !_allowMixedMode)
            {
                var protocol = TrackerClientFactory.GetProtocol(url);
                if (protocol != TrackerProtocol.I2p)
                {
                    _logger.LogDebug("Skipping non-I2P tracker {Url} for pure I2P torrent", url);
                    continue;
                }
            }

            try
            {
                var client = _trackerFactory.CreateClient(url);
                var state = new TrackerState(client, tier);
                var stats = new TrackerStatistics(url, client.Type, tier);

                if (_trackers.TryAdd(url, state))
                {
                    _statistics.TryAdd(url, stats);
                    _trackerUrls.Add(url);
                    _logger.LogDebug("Added tracker: {Url} (Tier {Tier})", url, tier);
                }

                tier++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create tracker client for: {Url}", url);
            }
        }
    }
    
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("TrackerManager is already running");

        if (_trackers.IsEmpty)
        {
            // Trackerless torrents (DHT-only) are valid — just mark as running and return.
            // Peer discovery will happen via DHT, PEX, and LSD instead.
            _logger.LogDebug("No trackers configured — torrent will rely on DHT/PEX/LSD for peer discovery");
            _isRunning = true;
            return Task.CompletedTask;
        }

        _isRunning = true;

        // Start scrape timer if enabled
        StartScrapeTimer();

        _logger.LogDebug("TrackerManager started with {Count} trackers", _trackers.Count);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the periodic scrape timer (no-op when scrape is disabled or a timer
    /// already exists). Shared by StartAsync and ResumeAnnouncing so pause→resume
    /// restores scraping instead of leaving it dead until the next engine start.
    /// </summary>
    private void StartScrapeTimer()
    {
        if (!TrackerConstants.EnableScrape || _scrapeTimer != null)
            return;

        var settings = _trackerMonitor.CurrentValue;
        var scrapeSeconds = Math.Max(settings.AutoScrapeInterval, settings.AutoScrapeMinInterval);
        var scrapeInterval = TimeSpan.FromSeconds(scrapeSeconds);
        _scrapeTimer = new Timer(
            async _ => await PerformScheduledScrapeAsync(),
            null,
            scrapeInterval,
            scrapeInterval);
    }

    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _logger.LogDebug("Stopping TrackerManager...");

        _isRunning = false;
        _stopCts.Cancel();

        // Stop timers
        _announceTimer?.Dispose();
        _scrapeTimer?.Dispose();
        _scrapeTimer = null;   // StartScrapeTimer guards on non-null — a disposed
                               // non-null reference would block recreation forever

        // Dispose all tracker clients
        foreach (var state in _trackers.Values)
        {
            try
            {
                state.Client.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing tracker client: {Url}", state.Client.TrackerUrl);
            }
        }

        _logger.LogDebug("TrackerManager stopped");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops periodic announcing and scraping WITHOUT disposing tracker clients —
    /// safe for pause. Unlike StopAsync (terminal: disposes clients, announces throw
    /// afterwards), the manager stays running: AnnounceStoppedAsync can still send the
    /// pause-time 'stopped' event, and the resume-time AnnounceStartedAsync revives the
    /// announce timer via ScheduleNextAnnounce. The scrape timer is revived by
    /// ResumeAnnouncing on resume.
    /// </summary>
    public void PauseAnnouncing()
    {
        _announcingPaused = true;
        _announceTimer?.Dispose();
        _announceTimer = null;
        _scrapeTimer?.Dispose();
        _scrapeTimer = null;
        _logger.LogDebug("Tracker announcing paused (timers stopped, clients kept)");
    }

    /// <summary>
    /// Symmetric counterpart of PauseAnnouncing: re-enables periodic announce
    /// scheduling and revives the scrape timer. The announce timer itself is
    /// recreated by ScheduleNextAnnounce after the resume-time 'started' announce.
    /// </summary>
    public void ResumeAnnouncing()
    {
        _announcingPaused = false;
        if (!_trackers.IsEmpty)
            StartScrapeTimer();
    }

    public async Task<TrackerAnnounceResult> AnnounceAsync(TrackerRequest request, CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
            throw new InvalidOperationException("TrackerManager is not running");

        // Trackerless torrents — nothing to announce to
        if (_trackers.IsEmpty)
            return new TrackerAnnounceResult { IsSuccess = true };

        await _announceLock.WaitAsync(cancellationToken);
        try
        {
            return await PerformAnnounceAsync(request, cancellationToken);
        }
        finally
        {
            _announceLock.Release();
        }
    }

    public Task<TrackerAnnounceResult> AnnounceStartedAsync(long left, CancellationToken cancellationToken = default)
    {
        _announcingPaused = false;   // resume re-enables periodic announce scheduling
        var request = TrackerRequest.CreateStarted(_infoHash, _peerId, _trackerMonitor.CurrentValue.ListenPort, left);
        request.NumWant = _trackerMonitor.CurrentValue.NumWant;
        request.Compact = TrackerConstants.UseCompactFormat;
        request.PeerKey = _peerKey;
        ApplySettingsToRequest(request);
        return AnnounceAsync(request, cancellationToken);
    }

    public Task<TrackerAnnounceResult> AnnounceStoppedAsync(long uploaded, long downloaded, long left, CancellationToken cancellationToken = default)
    {
        var adjustedDownloaded = AdjustDownloadedForAnnounce(downloaded);
        var request = TrackerRequest.CreateStopped(_infoHash, _peerId, _trackerMonitor.CurrentValue.ListenPort, uploaded, adjustedDownloaded, left);
        request.NumWant = 0; // Don't need peers when stopping
        request.Compact = TrackerConstants.UseCompactFormat;
        request.PeerKey = _peerKey;
        ApplySettingsToRequest(request);
        return AnnounceAsync(request, cancellationToken);
    }

    public Task<TrackerAnnounceResult> AnnounceCompletedAsync(long uploaded, long downloaded, CancellationToken cancellationToken = default)
    {
        var adjustedDownloaded = AdjustDownloadedForAnnounce(downloaded);
        var request = TrackerRequest.CreateCompleted(_infoHash, _peerId, _trackerMonitor.CurrentValue.ListenPort, uploaded, adjustedDownloaded);
        request.NumWant = _trackerMonitor.CurrentValue.NumWant;
        request.Compact = TrackerConstants.UseCompactFormat;
        request.PeerKey = _peerKey;
        ApplySettingsToRequest(request);
        return AnnounceAsync(request, cancellationToken);
    }

    public Task<TrackerAnnounceResult> AnnounceRegularAsync(long uploaded, long downloaded, long left, CancellationToken cancellationToken = default)
    {
        var adjustedDownloaded = AdjustDownloadedForAnnounce(downloaded);
        var request = TrackerRequest.CreateRegular(_infoHash, _peerId, _trackerMonitor.CurrentValue.ListenPort, uploaded, adjustedDownloaded, left);
        request.NumWant = _trackerMonitor.CurrentValue.NumWant;
        request.Compact = TrackerConstants.UseCompactFormat;
        request.PeerKey = _peerKey;
        ApplySettingsToRequest(request);
        return AnnounceAsync(request, cancellationToken);
    }

    /// <summary>
    /// Applies TrackerSettings overrides (AnnounceIp) to the request before sending.
    /// </summary>
    private void ApplySettingsToRequest(TrackerRequest request)
    {
        // Wire AnnounceIp: if set, override the request IP so trackers see the configured address
        if (!string.IsNullOrEmpty(_trackerMonitor.CurrentValue.AnnounceIp))
            request.Ip = _trackerMonitor.CurrentValue.AnnounceIp;

        // BEP 27: set private torrent flag and report external IPs to private trackers
        request.IsPrivateTorrent = _isPrivateTorrent;
        if (_isPrivateTorrent && _externalIpVoter != null)
        {
            var consensusIp = _externalIpVoter.GetConsensusIp();
            if (consensusIp != null)
            {
                if (consensusIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    request.Ipv4Address = consensusIp.ToString();
                else if (consensusIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    request.Ipv6Address = consensusIp.ToString();
            }
        }
    }

    /// <summary>
    /// Adjusts the downloaded byte count for tracker announces based on behavior settings.
    /// If ReportTrueDownloaded is true, redundant bytes are included in the downloaded count.
    /// If ReportRedundantBytes is false, redundant bytes are excluded (already the default behavior).
    /// </summary>
    private long AdjustDownloadedForAnnounce(long downloaded)
    {
        if (_trackerMonitor.CurrentValue.ReportTrueDownloaded && _trackerMonitor.CurrentValue.ReportRedundantBytes)
            return downloaded + RedundantBytes;

        return downloaded;
    }

    // TODO: Wire ApplyIpFilterToTrackers — TrackerSettings.ApplyIpFilterToTrackers exists but
    // TrackerManager does not currently have access to the session-level IpFilter to check
    // tracker IPs before connecting. Peer connections ARE filtered at the TransportConnector
    // level, so outgoing peer traffic respects the IP filter. Tracker-level filtering would
    // require resolving tracker hostnames and checking against the filter before announce,
    // which needs the IpFilter dependency threaded through from the orchestrator.

    private async Task<TrackerAnnounceResult> PerformAnnounceAsync(TrackerRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Announcing to {Count} trackers [Event: {Event}, ParallelMode: {ParallelMode}]",
            _trackers.Count, request.Event, _trackerMonitor.CurrentValue.ParallelAnnounceAcrossTiers ? "AllTiers" : "TierByTier");

        request.PeerKey = _peerKey;

        var responses = new ConcurrentDictionary<string, TrackerResponse>();
        var allPeers = new ConcurrentDictionary<string, TrackerPeer>(); // Use ConcurrentDictionary for thread-safe peer collection
        var stopwatch = Stopwatch.StartNew();

        // Get trackers sorted by tier and availability
        var sortedTrackers = _trackers.Values
            .Where(t => t.Client.IsAvailable)
            .OrderBy(t => t.Tier)
            .ThenBy(t => _trackerMonitor.CurrentValue.PreferUdpTrackers
                ? (t.Client.Type == Models.TrackerType.Udp ? 0 : 1)
                : 0)
            .ThenBy(t => t.ConsecutiveFailures)
            .ToList();

        if (sortedTrackers.Count == 0)
        {
            // All trackers marked unavailable (failure count >= 5).
            // Re-include all trackers regardless of availability — they may have failed
            // due to transient issues like I2P SAM session not being ready yet.
            sortedTrackers = _trackers.Values
                .OrderBy(t => t.Tier)
                .ThenBy(t => t.ConsecutiveFailures)
                .ToList();

            if (sortedTrackers.Count == 0)
            {
                _logger.LogWarning("No trackers configured");
                return TrackerAnnounceResult.CreateFailure("No trackers configured");
            }

            _logger.LogInformation("All trackers marked unavailable — retrying all {Count} trackers", sortedTrackers.Count);
        }

        // Choose announce strategy based on settings
        if (_trackerMonitor.CurrentValue.ParallelAnnounceAcrossTiers)
        {
            await PerformParallelAnnounceAsync(sortedTrackers, request, responses, allPeers, cancellationToken);
        }
        else
        {
            await PerformTieredAnnounceAsync(sortedTrackers, request, responses, allPeers, cancellationToken);
        }

        stopwatch.Stop();

        // Build result from all responses
        var result = TrackerAnnounceResult.FromResponses(new Dictionary<string, TrackerResponse>(responses));

        // Update state
        if (result.IsSuccess)
        {
            _lastSuccessfulAnnounce = DateTime.UtcNow;
            _baseAnnounceInterval = result.RecommendedInterval;
            _nextAnnounceInterval = _baseAnnounceInterval;
            _consecutiveAnnounceFailures = 0;
            _totalPeersDiscovered += result.Peers.Count;
            _totalSeeders = result.TotalSeeders;
            _totalLeechers = result.TotalLeechers;

            // Schedule next announce — but never after a 'stopped' announce: stopped
            // means pause/shutdown, and rescheduling here would revive the periodic
            // timer that PauseAnnouncing/StopAsync just disposed.
            if (request.Event != TrackerEvent.Stopped)
                ScheduleNextAnnounce(result.RecommendedInterval);

            // Fire peers discovered event
            if (result.Peers.Count > 0)
            {
                PeersDiscovered?.Invoke(this, new PeersDiscoveredEventArgs(
                    "multiple",
                    result.Peers,
                    result.TotalSeeders,
                    result.TotalLeechers));
            }
        }
        else
        {
            _consecutiveAnnounceFailures++;
            if (request.Event != TrackerEvent.Stopped)
                ScheduleNextAnnounce(_baseAnnounceInterval);
        }

        _logger.LogInformation("Announce completed in {Duration}ms - {Success}/{Total} trackers, {Peers} unique peers",
            stopwatch.ElapsedMilliseconds,
            result.SuccessfulTrackers,
            result.SuccessfulTrackers + result.FailedTrackers,
            result.Peers.Count);

        return result;
    }

    /// <summary>
    /// Performs parallel announces to ALL trackers across all tiers simultaneously.
    /// Uses early return when enough peers are found for regular announces.
    /// Based on optimized patterns from libtorrent.
    /// </summary>
    private async Task PerformParallelAnnounceAsync(
        List<TrackerState> trackers,
        TrackerRequest request,
        ConcurrentDictionary<string, TrackerResponse> responses,
        ConcurrentDictionary<string, TrackerPeer> allPeers,
        CancellationToken cancellationToken)
    {
        // Use semaphore to limit concurrent announces
        using var semaphore = new SemaphoreSlim(_trackerMonitor.CurrentValue.MaxParallelAnnounces);

        // Create all announce tasks
        var announceTasks = trackers.Select(async trackerState =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await AnnounceToTrackerAsync(trackerState, request, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        // For regular announces (not started/stopped/completed), use early return
        bool isRegularAnnounce = request.Event == TrackerEvent.None;
        bool canReturnEarly = isRegularAnnounce && TrackerConstants.EarlyReturnTimeoutSeconds > 0;

        if (canReturnEarly)
        {
            // Process results as they complete, return early if we have enough peers
            var remainingTasks = new List<Task<(string url, TrackerResponse response, TimeSpan duration)>>(announceTasks);
            var earlyReturnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var earlyReturnDeadline = DateTime.UtcNow.AddSeconds(TrackerConstants.EarlyReturnTimeoutSeconds);

            while (remainingTasks.Count > 0)
            {
                // Wait for any task to complete, with timeout
                var timeout = earlyReturnDeadline - DateTime.UtcNow;
                if (timeout <= TimeSpan.Zero)
                {
                    _logger.LogDebug("Early return timeout reached with {Peers} peers from {Responses} trackers",
                        allPeers.Count, responses.Count);
                    break;
                }

                var timeoutTask = Task.Delay(timeout, earlyReturnCts.Token);
                var completedTask = await Task.WhenAny(remainingTasks.Cast<Task>().Append(timeoutTask));

                if (completedTask == timeoutTask)
                {
                    _logger.LogDebug("Early return timeout reached with {Peers} peers", allPeers.Count);
                    break;
                }

                // Find and process the completed task
                var completedAnnounceTask = remainingTasks.FirstOrDefault(t => t.IsCompleted);
                if (completedAnnounceTask != null)
                {
                    remainingTasks.Remove(completedAnnounceTask);

                    try
                    {
                        var result = await completedAnnounceTask;
                        ProcessSingleResult(result, responses, allPeers);

                        // Check if we have enough peers
                        if (allPeers.Count >= _trackerMonitor.CurrentValue.NumWant)
                        {
                            _logger.LogDebug("Got enough peers ({Count}), returning early", allPeers.Count);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Announce task failed: {Error}", ex.Message);
                    }
                }
            }

            // Cancel the timeout and let remaining tasks complete in background
            // (their results will be ignored, but they still update tracker state)
            earlyReturnCts.Cancel();
        }
        else
        {
            // For event announces (started/stopped/completed), wait for all trackers
            var results = await Task.WhenAll(announceTasks);
            foreach (var result in results)
            {
                ProcessSingleResult(result, responses, allPeers);
            }
        }
    }

    /// <summary>
    /// Performs tiered announces (original behavior) - waits for each tier to complete before moving to the next.
    /// </summary>
    private async Task PerformTieredAnnounceAsync(
        List<TrackerState> trackers,
        TrackerRequest request,
        ConcurrentDictionary<string, TrackerResponse> responses,
        ConcurrentDictionary<string, TrackerPeer> allPeers,
        CancellationToken cancellationToken)
    {
        var currentTier = -1;
        var tierTasks = new List<Task<(string url, TrackerResponse response, TimeSpan duration)>>();

        foreach (var trackerState in trackers)
        {
            // If we move to a new tier and already have peers, we can be less aggressive
            if (trackerState.Tier != currentTier)
            {
                // Wait for current tier to complete
                if (tierTasks.Count > 0)
                {
                    var tierResults = await Task.WhenAll(tierTasks);
                    foreach (var result in tierResults)
                    {
                        ProcessSingleResult(result, responses, allPeers);
                    }

                    // If we got enough peers from higher-priority tier, skip lower tiers for regular announces
                    if (request.Event == TrackerEvent.None && allPeers.Count >= _trackerMonitor.CurrentValue.NumWant)
                    {
                        _logger.LogDebug("Got enough peers ({Count}) from tier {Tier}, skipping lower tiers",
                            allPeers.Count, currentTier);
                        break;
                    }

                    tierTasks.Clear();
                }

                currentTier = trackerState.Tier;
            }

            // Add announce task for this tracker
            tierTasks.Add(AnnounceToTrackerAsync(trackerState, request, cancellationToken));
        }

        // Process remaining tier
        if (tierTasks.Count > 0)
        {
            var tierResults = await Task.WhenAll(tierTasks);
            foreach (var result in tierResults)
            {
                ProcessSingleResult(result, responses, allPeers);
            }
        }
    }

    /// <summary>
    /// Processes a single announce result into the response and peer collections.
    /// </summary>
    private void ProcessSingleResult(
        (string url, TrackerResponse response, TimeSpan duration) result,
        ConcurrentDictionary<string, TrackerResponse> responses,
        ConcurrentDictionary<string, TrackerPeer> allPeers)
    {
        responses[result.url] = result.response;

        if (result.response.IsSuccess && result.response.Peers != null)
        {
            foreach (var peer in result.response.Peers)
            {
                // Use endpoint string as key to deduplicate peers
                var key = $"{peer.Ip}:{peer.Port}";
                allPeers.TryAdd(key, peer);
            }
        }
    }

    private async Task<(string url, TrackerResponse response, TimeSpan duration)> AnnounceToTrackerAsync(
        TrackerState state,
        TrackerRequest request,
        CancellationToken cancellationToken)
    {
        var url = state.Client.TrackerUrl;
        var stopwatch = Stopwatch.StartNew();
        request.PeerKey = _peerKey;

        try
        {
            _logger.LogDebug("Announcing to {Url}...", url);

            var response = await state.Client.AnnounceAsync(request, cancellationToken);
            stopwatch.Stop();

            if (response.IsSuccess)
            {
                state.RecordSuccess();
                _statistics[url].RecordSuccess(
                    response.Peers.Count,
                    response.Complete,
                    response.Incomplete,
                    response.Interval,
                    stopwatch.Elapsed);

                _logger.LogDebug("Tracker {Url} returned {Peers} peers, interval {Interval}s",
                    url, response.Peers.Count, response.Interval);

                AnnounceCompleted?.Invoke(this, AnnounceCompletedEventArgs.CreateSuccess(
                    url, response.Peers.Count, response.Interval, stopwatch.Elapsed));
            }
            else
            {
                state.RecordFailure();
                _statistics[url].RecordFailure(stopwatch.Elapsed);

                _logger.LogWarning("Tracker {Url} failed: {Reason}", url, response.FailureReason);

                TrackerFailed?.Invoke(this, new TrackerFailedEventArgs(
                    url, response.FailureReason, null, state.ConsecutiveFailures));

                AnnounceCompleted?.Invoke(this, AnnounceCompletedEventArgs.CreateFailure(
                    url, response.FailureReason, stopwatch.Elapsed));
            }

            return (url, response, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            state.RecordFailure();
            _statistics[url].RecordFailure(stopwatch.Elapsed);

            _logger.LogError(ex, "Error announcing to {Url}", url);

            TrackerFailed?.Invoke(this, new TrackerFailedEventArgs(
                url, ex.Message, ex, state.ConsecutiveFailures));

            AnnounceCompleted?.Invoke(this, AnnounceCompletedEventArgs.CreateFailure(
                url, ex.Message, stopwatch.Elapsed));

            return (url, TrackerResponse.CreateFailure(ex.Message, url), stopwatch.Elapsed);
        }
    }

    private void ScheduleNextAnnounce(int intervalSeconds)
    {
        if (_announcingPaused)
            return;
        var effectiveInterval = intervalSeconds;
        if (_consecutiveAnnounceFailures > 0)
        {
            var backoffPercent = _trackerMonitor.CurrentValue.TrackerBackoff;
            var backoffFactor = Math.Pow(backoffPercent / 100.0, _consecutiveAnnounceFailures);
            effectiveInterval = (int)(_baseAnnounceInterval * backoffFactor);
            effectiveInterval = Math.Min(effectiveInterval, 3600); // cap at 1 hour
        }

        var interval = Math.Max(effectiveInterval, _trackerMonitor.CurrentValue.MinAnnounceInterval);

        _announceTimer?.Dispose();
        _announceTimer = new Timer(
            async _ => await PerformScheduledAnnounceAsync(),
            null,
            TimeSpan.FromSeconds(interval),
            Timeout.InfiniteTimeSpan);

        _logger.LogDebug("Next announce scheduled in {Interval} seconds (backoff failures: {Failures})",
            interval, _consecutiveAnnounceFailures);
    }

    private async Task PerformScheduledAnnounceAsync()
    {
        if (!_isRunning || _stopCts.IsCancellationRequested)
            return;

        try
        {
            _logger.LogDebug("Performing scheduled announce...");

            // For scheduled announces, we don't have current upload/download stats
            // The caller should use AnnounceRegularAsync with actual stats
            // This is a fallback with zeros - in practice, the TorrentEngine should manage this
            await AnnounceRegularAsync(0, 0, 0, _stopCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during scheduled announce");
        }
    }

    public async Task<TrackerScrapeResult> ScrapeAsync(CancellationToken cancellationToken = default)
    {
        if (!TrackerConstants.EnableScrape)
        {
            _logger.LogDebug("Scraping is disabled in settings");
            return TrackerScrapeResult.CreateFailure("Scraping disabled");
        }

        _logger.LogDebug("Scraping {Count} trackers...", _trackers.Count);

        var responses = new Dictionary<string, ScrapeResponse>();

        var scrapeTasks = _trackers.Values
            .Where(t => t.Client.IsAvailable)
            .Select(async state =>
            {
                try
                {
                    var response = await state.Client.ScrapeAsync(_infoHash, cancellationToken);
                    if (response.IsSuccess)
                    {
                        _statistics[state.Client.TrackerUrl].RecordScrape(
                            response.Complete, response.Incomplete, response.Downloaded);
                    }
                    return (state.Client.TrackerUrl, response);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Scrape failed for {Url}", state.Client.TrackerUrl);
                    return (state.Client.TrackerUrl, ScrapeResponse.CreateFailure(ex.Message));
                }
            });

        var results = await Task.WhenAll(scrapeTasks);

        foreach (var (url, response) in results)
        {
            responses[url] = response;
        }

        var result = TrackerScrapeResult.FromResponses(responses);

        // Fire event
        ScrapeCompleted?.Invoke(this, new ScrapeCompletedEventArgs(
            result.IsSuccess,
            result.TotalSeeders,
            result.TotalLeechers,
            result.SuccessfulTrackers,
            result.FailedTrackers));

        _logger.LogInformation("Scrape completed - {Success}/{Total} trackers, {Seeders} seeders, {Leechers} leechers",
            result.SuccessfulTrackers,
            result.SuccessfulTrackers + result.FailedTrackers,
            result.TotalSeeders,
            result.TotalLeechers);

        return result;
    }

    private async Task PerformScheduledScrapeAsync()
    {
        if (!_isRunning || _stopCts.IsCancellationRequested)
            return;

        try
        {
            await ScrapeAsync(_stopCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during scheduled scrape");
        }
    }

    public bool AddTracker(string trackerUrl)
    {
        if (string.IsNullOrWhiteSpace(trackerUrl))
            return false;

        if (_trackers.ContainsKey(trackerUrl))
        {
            _logger.LogDebug("Tracker already exists: {Url}", trackerUrl);
            return false;
        }

        if (!_trackerFactory.IsSupported(trackerUrl))
        {
            _logger.LogWarning("Tracker protocol not supported: {Url}", trackerUrl);
            return false;
        }

        try
        {
            var client = _trackerFactory.CreateClient(trackerUrl);
            var maxTier = _trackers.Values.Any() ? _trackers.Values.Max(t => t.Tier) + 1 : 0;
            var state = new TrackerState(client, maxTier);
            var stats = new TrackerStatistics(trackerUrl, client.Type, maxTier);

            if (_trackers.TryAdd(trackerUrl, state))
            {
                _statistics.TryAdd(trackerUrl, stats);
                _trackerUrls.Add(trackerUrl);
                _logger.LogDebug("Added tracker: {Url} (Tier {Tier})", trackerUrl, maxTier);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add tracker: {Url}", trackerUrl);
        }

        return false;
    }

    public bool RemoveTracker(string trackerUrl)
    {
        if (string.IsNullOrWhiteSpace(trackerUrl))
            return false;

        if (_trackers.TryRemove(trackerUrl, out var state))
        {
            _statistics.TryRemove(trackerUrl, out _);
            _trackerUrls.Remove(trackerUrl);

            try
            {
                state.Client.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing tracker client: {Url}", trackerUrl);
            }

            _logger.LogDebug("Removed tracker: {Url}", trackerUrl);
            return true;
        }

        return false;
    }

    public async Task ForceReannounceAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
        {
            _logger.LogDebug("Cannot force reannounce: tracker manager not running");
            return;
        }

        _logger.LogDebug("Force reannounce triggered");

        // Cancel the current scheduled timer and announce immediately
        _announceTimer?.Dispose();
        _announceTimer = null;

        try
        {
            await AnnounceRegularAsync(0, 0, 0, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Force reannounce failed");
        }
    }

    public TrackerStatistics GetTrackerStatistics(string trackerUrl)
    {
        return _statistics.TryGetValue(trackerUrl, out var stats) ? stats : null;
    }

    public IReadOnlyDictionary<string, TrackerStatistics> GetAllTrackerStatistics()
    {
        return new Dictionary<string, TrackerStatistics>(_statistics);
    }

    public IPeerConnection GetPeer(PeerInfo peerInfo)
    {
        // TrackerManager doesn't manage peer connections directly
        // This should be handled by PeerManager
        throw new NotSupportedException("TrackerManager does not manage peer connections. Use PeerManager instead.");
    }

    public bool IsConnected(PeerInfo peerInfo)
    {
        // TrackerManager doesn't manage peer connections directly
        throw new NotSupportedException("TrackerManager does not manage peer connections. Use PeerManager instead.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _stopCts.Cancel();
        _announceTimer?.Dispose();
        _scrapeTimer?.Dispose();
        _announceLock?.Dispose();

        foreach (var state in _trackers.Values)
        {
            try
            {
                state.Client.Dispose();
            }
            catch
            {
            }
        }

        _trackers.Clear();
        _statistics.Clear();
        _stopCts.Dispose();

        _logger.LogDebug("TrackerManager disposed");
    }
}