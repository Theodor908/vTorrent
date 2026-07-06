using System;

using System.Collections.Generic;

using System.Linq;

using System.Threading;

using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;

using vTorrent.Core.PeerCommunication.Events;
using PeerChokeChangedEventArgs = vTorrent.Core.Events.PeerChokeChangedEventArgs;

using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.Engine;
using vTorrent.Core.Orchestration;

namespace vTorrent.Core.Upload;

/// <summary>

/// Choking algorithm based on libtorrent's implementation.

///

/// Key features:

/// 1. Rate-based slot calculation - dynamically adjusts upload slots based on actual rates

/// 2. Tit-for-Tat (TFT) - reward peers who contribute

/// 3. Optimistic unchoking - discover new good peers

/// 4. Anti-leech algorithm for seeding - prefer peers who need help most

/// 5. Snubbing detection - handle unresponsive peers

///

/// References:

/// - libtorrent choker.cpp

/// - BitTorrent specification

/// - "Improving BitTorrent: A Simple Approach" (anti-leech algorithm)

/// </summary>

public partial class ChokingManager : IChokingManager, IMessageHandler, IDisposable

{

    private readonly ILogger<ChokingManager> _logger;

    private readonly IPeerManager _peerManager;

    private readonly IStatisticsTracker _statisticsTracker;

    private readonly Func<bool> _isSeedingFunc;

    private readonly Func<int> _getTotalPiecesFunc;

    private readonly Func<IPeerConnection, int> _getPeerPieceCountFunc;

    private readonly IOptionsMonitor<BehaviorSettings> _behaviorMonitor;

    private readonly IOptionsMonitor<PeerSettings> _peerSettingsMonitor;

    private readonly TorrentSettings? _torrentSettings;

    private readonly UnchokeAllocator? _unchokeAllocator;

    // Choking algorithm selection

    public ChokingAlgorithm Algorithm { get; set; } = ChokingAlgorithm.RateBased;

    public SeedChokingAlgorithm SeedAlgorithm { get; set; } = SeedChokingAlgorithm.FastestUpload;

    // Configuration

    private int _minUploadSlots = 4;  // Increased from 2 for better reciprocity

    private int _maxUploadSlots = 12;

    private int _optimisticSlots = 1;

    private int _rateThresholdInitial = 4096;  // 4 KB/s initial threshold

    private int _rateThresholdIncrement = 2048;  // +2 KB/s per slot

    private TimeSpan _rechokingInterval = TimeSpan.FromSeconds(15);  // libtorrent default

    private TimeSpan _optimisticRotationInterval = TimeSpan.FromSeconds(30);

    private TimeSpan _snubbedTimeout = TimeSpan.FromSeconds(60);  // 1 minute (was 2)

    // State

    private readonly HashSet<IPeerConnection> _currentlyUnchoked = new();

    private readonly HashSet<IPeerConnection> _interestedPeers = new();

    private readonly Dictionary<IPeerConnection, DateTime> _lastDataReceived = new();

    private readonly HashSet<IPeerConnection> _snubbedPeers = new();

    private IPeerConnection _optimisticPeer;

    private DateTime _lastOptimisticRotation = DateTime.MinValue;

    private DateTime _lastRechoke = DateTime.MinValue;

    private int _currentUploadSlots;

    private readonly object _chokeLock = new();

    // Reusable buffers to avoid per-cycle allocations

    private readonly List<IPeerConnection> _peerSnapshot = new();

    private readonly List<IPeerConnection> _interestedSnapshot = new();

    private (IPeerConnection peer, double rate, bool snubbed)[] _sortBuffer;

    // Adaptive scoring
    private readonly PeerScoreTracker _scoreTracker = new();

    // Background task

    private CancellationTokenSource _cts;

    private Task _rechokingTask;

    private bool _disposed;

    // Events

    public event EventHandler<PeerChokeChangedEventArgs> PeerChoked;

    public event EventHandler<PeerChokeChangedEventArgs> PeerUnchoked;

    /// <summary>
    /// Fired at the end of each rechoke cycle. UploadCoordinator subscribes to
    /// trigger send-buffer watermark recalculation after slot changes.
    /// </summary>
    public event Action? RechokeCycleCompleted;

    // Properties

    public long TotalUploaded => _statisticsTracker.TotalUploaded;

    public long TotalDownloaded => _statisticsTracker.TotalDownloaded;

    public int UnchokedPeerCount

    {

        get { lock (_chokeLock) return _currentlyUnchoked.Count; }

    }

    public int InterestedPeerCount

    {

        get { lock (_chokeLock) return _interestedPeers.Count; }

    }

    public int CurrentUploadSlots => _currentUploadSlots;

    public int SnubbedPeerCount

    {

        get { lock (_chokeLock) return _snubbedPeers.Count; }

    }

    public ChokingManager(

        IPeerManager peerManager,

        IStatisticsTracker statisticsTracker,

        Func<bool> isSeedingFunc,

        ILogger<ChokingManager> logger,

        IOptionsMonitor<BehaviorSettings> behaviorMonitor = null,

        IOptionsMonitor<PeerSettings> peerSettingsMonitor = null,

        TorrentSettings? torrentSettings = null,

        Func<int> getTotalPiecesFunc = null,

        Func<IPeerConnection, int> getPeerPieceCountFunc = null,

        UnchokeAllocator? unchokeAllocator = null)

    {

        _peerManager = peerManager ?? throw new ArgumentNullException(nameof(peerManager));

        _statisticsTracker = statisticsTracker ?? throw new ArgumentNullException(nameof(statisticsTracker));

        _isSeedingFunc = isSeedingFunc ?? throw new ArgumentNullException(nameof(isSeedingFunc));

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _behaviorMonitor = behaviorMonitor;

        _peerSettingsMonitor = peerSettingsMonitor;

        _torrentSettings = torrentSettings;

        _getTotalPiecesFunc = getTotalPiecesFunc;

        _getPeerPieceCountFunc = getPeerPieceCountFunc;

        _unchokeAllocator = unchokeAllocator;

        _currentUploadSlots = _minUploadSlots;

        _peerManager.PeerConnected += OnPeerConnected;

        _peerManager.PeerDisconnected += OnPeerDisconnected;

        _logger.LogDebug(

            "ChokingManager initialized - Algorithm: {Algorithm}, SeedAlgorithm: {SeedAlgorithm}, " +

            "Slots: {Min}-{Max}, Optimistic: {Opt}",

            Algorithm, SeedAlgorithm, _minUploadSlots, _maxUploadSlots, _optimisticSlots);

    }

    /// <summary>

    /// Configures the choking manager settings.

    /// </summary>

    public void Configure(

        ChokingAlgorithm algorithm = ChokingAlgorithm.RateBased,

        SeedChokingAlgorithm seedAlgorithm = SeedChokingAlgorithm.FastestUpload,

        int minSlots = 2,

        int maxSlots = 12,

        int optimisticSlots = 1,

        TimeSpan? rechokingInterval = null,

        TimeSpan? snubbedTimeout = null,

        TimeSpan? optimisticRotationInterval = null)

    {

        Algorithm = algorithm;

        SeedAlgorithm = seedAlgorithm;

        _minUploadSlots = minSlots;

        _maxUploadSlots = maxSlots;

        _optimisticSlots = optimisticSlots;

        if (rechokingInterval.HasValue) _rechokingInterval = rechokingInterval.Value;

        if (snubbedTimeout.HasValue) _snubbedTimeout = snubbedTimeout.Value;

        if (optimisticRotationInterval.HasValue)

            _optimisticRotationInterval = optimisticRotationInterval.Value;

        _logger.LogDebug("ChokingManager reconfigured - Algorithm: {Algorithm}, Slots: {Min}-{Max}",

            algorithm, minSlots, maxSlots);

    }

    public Task StartAsync(CancellationToken cancellationToken = default)

    {

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _rechokingTask = Task.Run(RechokingLoopAsync, _cts.Token);

        // Initial bootstrap unchoke after a short delay

        _ = Task.Run(async () =>

        {

            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

            await InitialBootstrapUnchokeAsync(cancellationToken).ConfigureAwait(false);

        }, cancellationToken);

        _logger.LogDebug("ChokingManager started");

        return Task.CompletedTask;

    }

    public async Task StopAsync()

    {

        _cts?.Cancel();

        if (_rechokingTask != null)

        {

            try { await _rechokingTask.ConfigureAwait(false); }

            catch (OperationCanceledException) { }

        }

        _logger.LogDebug("ChokingManager stopped - Uploaded: {Up}, Downloaded: {Down}",

            TorrentUtilities.FormatBytes(TotalUploaded),

            TorrentUtilities.FormatBytes(TotalDownloaded));

    }

    public void RegisterHandlers(PeerMessageRouter router)

    {

        router.RegisterHandler(MessageType.Interested, HandlePeerInterestedAsync);

        router.RegisterHandler(MessageType.NotInterested, HandlePeerNotInterestedAsync);

        router.RegisterHandler(MessageType.Piece, HandlePieceReceivedAsync);

    }

    /// <summary>

    /// Records that we received data from a peer (for snubbing detection).

    /// </summary>

    public void RecordDataReceived(IPeerConnection peer)

    {

        lock (_chokeLock)

        {

            _lastDataReceived[peer] = DateTime.UtcNow;

            // Clear snubbed status if peer is now sending data

            if (_snubbedPeers.Remove(peer))

            {

                LogPeerNoLongerSnubbed(_logger, peer.PeerInfo.EndPoint);

            }

        }

    }

    /// <summary>

    /// Checks if a peer is snubbed (hasn't sent data despite being unchoked by them).

    /// </summary>

    public bool IsPeerSnubbed(IPeerConnection peer)

    {

        lock (_chokeLock)

        {

            return _snubbedPeers.Contains(peer);

        }

    }

    private async Task InitialBootstrapUnchokeAsync(CancellationToken ct)

    {

        var peers = _peerManager.ConnectedPeers.Take(_maxUploadSlots).ToList();

        if (!peers.Any()) return;

        _logger.LogDebug("BOOTSTRAP: Temporarily unchoking {Count} peers", peers.Count);

        var tasks = new List<Task>();

        lock (_chokeLock)

        {

            foreach (var peer in peers)

            {

                if (_currentlyUnchoked.Add(peer))
                {
                    if (_unchokeAllocator != null && !_unchokeAllocator.TryAcquireUnchokeSlot())
                    {
                        _currentlyUnchoked.Remove(peer);
                        _logger.LogDebug("Global unchoke cap reached ({Max}) during bootstrap, stopping",
                            _unchokeAllocator.MaxGlobalUnchokeSlots);
                        break;
                    }

                    tasks.Add(UnchokePeerAsync(peer));
                }

            }

        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

    }

    public Task HandlePeerInterestedAsync(IPeerConnection peer, PeerMessage message)

    {

        OnPeerInterested(peer);

        return Task.CompletedTask;

    }

    public Task HandlePeerNotInterestedAsync(IPeerConnection peer, PeerMessage message)

    {

        OnPeerNotInterested(peer);

        return Task.CompletedTask;

    }

    public Task HandlePieceReceivedAsync(IPeerConnection peer, PeerMessage message)

    {

        RecordDataReceived(peer);

        return Task.CompletedTask;

    }

    public void OnPeerInterested(IPeerConnection peer)

    {

        lock (_chokeLock)

        {

            _interestedPeers.Add(peer);

        }

        LogPeerIsInterested(_logger, peer.PeerInfo.EndPoint, InterestedPeerCount);

        _ = TryImmediateUnchokeAsync(peer);

    }

    public void OnPeerNotInterested(IPeerConnection peer)

    {

        lock (_chokeLock)

        {

            _interestedPeers.Remove(peer);

            if (_currentlyUnchoked.Remove(peer))

            {

                _unchokeAllocator?.ReleaseUnchokeSlot();

                _ = ChokePeerAsync(peer);

                LogChokedNotInterested(_logger, peer.PeerInfo.EndPoint);

            }

            if (_optimisticPeer == peer)

                _optimisticPeer = null;

        }

    }

    private async Task TryImmediateUnchokeAsync(IPeerConnection peer)

    {

        // Fast peer bypass - immediately unchoke very fast peers

        var downloadRate = _statisticsTracker.GetPeerDownloadRate(peer);

        lock (_chokeLock)

        {

            if (_currentlyUnchoked.Contains(peer))

                return;

            // Fast peer threshold: > 768 KB/s

            if (downloadRate > 768 * 1024)

            {

                // Allow exceeding max slots by 2 for fast peers

                if (_currentlyUnchoked.Count < _currentUploadSlots + 2)

                {

                    if (_unchokeAllocator != null && !_unchokeAllocator.TryAcquireUnchokeSlot())
                    {
                        LogGlobalUnchokeCapSkipFastUnchoke(_logger, _unchokeAllocator.MaxGlobalUnchokeSlots, peer.PeerInfo.EndPoint);
                        return;
                    }

                    _currentlyUnchoked.Add(peer);

                    _ = UnchokePeerAsync(peer);

                    LogFastUnchoke(_logger, peer.PeerInfo.EndPoint,
                        TorrentUtilities.FormatRate(downloadRate),
                        _currentlyUnchoked.Count, _currentUploadSlots);

                }

            }

        }

    }

    public bool IsPeerUnchoked(IPeerConnection peer)

    {

        lock (_chokeLock)

            return _currentlyUnchoked.Contains(peer);

    }

    public void OnLocalPieceCompleted(int pieceIndex)

    {

        _statisticsTracker.RecordPieceCompleted();

    }

    private async Task RechokingLoopAsync()

    {

        while (!_cts.Token.IsCancellationRequested)

        {

            try

            {

                await Task.Delay(_rechokingInterval, _cts.Token).ConfigureAwait(false);

                await RechokePeersAsync().ConfigureAwait(false);

                UpdateSnubbedPeers();

            }

            catch (OperationCanceledException) { break; }

            catch (Exception ex) { _logger.LogError(ex, "Rechoking loop error"); }

        }

    }

    private void UpdateSnubbedPeers()

    {

        var now = DateTime.UtcNow;

        lock (_chokeLock)

        {

            foreach (var peer in _peerManager.ConnectedPeers)

            {

                // Only check peers we're interested in and that have unchoked us

                if (!peer.IsInterested || peer.IsChoked)

                    continue;

                if (_lastDataReceived.TryGetValue(peer, out var lastData))

                {

                    if (now - lastData > _snubbedTimeout)

                    {

                        if (_snubbedPeers.Add(peer))

                        {

                            LogPeerMarkedSnubbed(_logger, peer.PeerInfo.EndPoint, _snubbedTimeout.TotalSeconds);

                        }

                    }

                }

                else

                {

                    // Never received data, check connection time

                    if (now - peer.ConnectedAt > _snubbedTimeout)

                    {

                        if (_snubbedPeers.Add(peer))

                        {

                            LogPeerMarkedSnubbedSinceConnection(_logger, peer.PeerInfo.EndPoint);

                        }

                    }

                }

            }

        }

    }

    private async Task RechokePeersAsync()

    {

        _peerSnapshot.Clear();

        foreach (var p in _peerManager.ConnectedPeers)

            _peerSnapshot.Add(p);

        _interestedSnapshot.Clear();

        lock (_chokeLock)

        {

            foreach (var p in _peerSnapshot)

            {

                if (p.IsConnected && _interestedPeers.Contains(p))

                    _interestedSnapshot.Add(p);

            }

        }

        if (_interestedSnapshot.Count == 0)

        {

            await ChokeAllAsync().ConfigureAwait(false);

            return;

        }

        // Resolve settings lazily each rechoke cycle (monitors may be null in tests)
        if (_behaviorMonitor != null)
        {
            var behavior = _behaviorMonitor.CurrentValue;
            Algorithm = SettingsResolver.Resolve(_torrentSettings?.ChokingAlgorithm, behavior.ChokingAlgorithm);
            SeedAlgorithm = SettingsResolver.Resolve(_torrentSettings?.SeedChokingAlgorithm, behavior.SeedChokingAlgorithm);
        }
        if (_peerSettingsMonitor != null)
        {
            var effectiveOptimisticSlots = SettingsResolver.Resolve(
                _torrentSettings?.NumOptimisticUnchokeSlots ?? -1,
                _peerSettingsMonitor.CurrentValue.NumOptimisticUnchokeSlots);
            // 0 = auto (20% of upload slots); use _optimisticSlots if configured explicitly via Configure()
            if (effectiveOptimisticSlots > 0)
                _optimisticSlots = effectiveOptimisticSlots;
        }

        // Record adaptive samples before slot calculation
        if (Algorithm == ChokingAlgorithm.Adaptive)
        {
            foreach (var peer in _peerSnapshot)
            {
                var rate = _statisticsTracker.GetPeerDownloadRate(peer);
                var pieceCount = _getPeerPieceCountFunc?.Invoke(peer) ?? 0;
                _scoreTracker.RecordSample(peer, rate, pieceCount);
            }
        }

        // Calculate upload slots

        _currentUploadSlots = CalculateUploadSlots(_interestedSnapshot);

        // Select regular unchokes

        var regularUnchokes = SelectRegularUnchokes(_interestedSnapshot, _currentUploadSlots - _optimisticSlots);

        // Rotate optimistic unchoke

        RotateOptimisticUnchoke(_interestedSnapshot, regularUnchokes);

        // Build target set

        var targetUnchoked = new HashSet<IPeerConnection>(regularUnchokes);

        if (_optimisticPeer != null)

            targetUnchoked.Add(_optimisticPeer);

        await ApplyChokingDecisionsAsync(_peerSnapshot, targetUnchoked).ConfigureAwait(false);

        PruneRedundantConnection(_peerSnapshot);

        _lastRechoke = DateTime.UtcNow;

        RechokeCycleCompleted?.Invoke();

    }

    /// <summary>
    /// Disconnects at most one redundant connection per rechoke cycle.
    /// Matches libtorrent's disconnect_if_redundant behavior.
    /// Rules:
    /// 1. Seed-to-seed: both sides complete, neither benefits
    /// 2. Upload-only peer that is a seed and we are seeding — no data exchange possible
    /// </summary>
    private void PruneRedundantConnection(IReadOnlyList<IPeerConnection> peers)
    {
        if (!_isSeedingFunc())
            return;

        var now = DateTime.UtcNow;
        var gracePeriod = TimeSpan.FromSeconds(30);

        foreach (var peer in peers)
        {
            if (!peer.IsConnected)
                continue;

            // Grace period: don't prune newly connected peers
            // (they may send INTERESTED after bitfield exchange)
            var connectionAge = now - peer.ConnectedAt;
            if (connectionAge < gracePeriod)
                continue;

            // Rule 1: Seed-to-seed — both sides are complete, neither benefits
            if (peer.IsSeed)
            {
                _logger.LogDebug("Pruning redundant seed-to-seed connection: {Peer}", peer.PeerInfo?.EndPoint);
                _ = peer.DisconnectAsync();
                return; // Max 1 per cycle
            }
        }
    }

    /// <summary>

    /// Calculates upload slots dynamically based on actual upload rates.

    /// libtorrent rate_based_choker algorithm.

    /// </summary>

    private int CalculateUploadSlots(List<IPeerConnection> interestedPeers)

    {

        if (Algorithm == ChokingAlgorithm.FixedSlots)

        {

            return _maxUploadSlots;

        }

        if (Algorithm == ChokingAlgorithm.Adaptive)
        {
            return CalculateAdaptiveSlots(interestedPeers);
        }

        // Rate-based calculation

        // Sort peers by upload rate descending using reusable buffer

        if (_sortBuffer == null || _sortBuffer.Length < interestedPeers.Count)

            _sortBuffer = new (IPeerConnection, double, bool)[Math.Max(interestedPeers.Count, 16)];

        for (int i = 0; i < interestedPeers.Count; i++)

            _sortBuffer[i] = (interestedPeers[i], _statisticsTracker.GetPeerUploadRate(interestedPeers[i]), false);

        Array.Sort(_sortBuffer, 0, interestedPeers.Count,

            Comparer<(IPeerConnection, double, bool)>.Create((a, b) => b.Item2.CompareTo(a.Item2)));

        int slots = 0;

        int threshold = _rateThresholdInitial;

        for (int i = 0; i < interestedPeers.Count; i++)

        {

            var rate = _sortBuffer[i].rate;

            // Convert rate to per-interval rate

            var intervalRate = rate * _rechokingInterval.TotalSeconds;

            if (rate < threshold)

                break;

            slots++;

            threshold += _rateThresholdIncrement;

        }

        // Ensure at least minimum slots

        slots = Math.Max(slots + 1, _minUploadSlots);

        slots = Math.Min(slots, _maxUploadSlots);

        return slots;

    }

    private List<IPeerConnection> SelectRegularUnchokes(List<IPeerConnection> interestedPeers, int regularSlots)

    {

        if (Algorithm == ChokingAlgorithm.Adaptive)
        {
            return SelectAdaptiveUnchokes(interestedPeers, regularSlots);
        }

        bool seeding = _isSeedingFunc();

        if (seeding)

        {

            return SelectSeedingUnchokes(interestedPeers, regularSlots);

        }

        else

        {

            return SelectDownloadingUnchokes(interestedPeers, regularSlots);

        }

    }

    /// <summary>

    /// Selects peers to unchoke while downloading (Tit-for-Tat).

    /// Prefer peers who send us data the fastest.

    /// </summary>

    private List<IPeerConnection> SelectDownloadingUnchokes(List<IPeerConnection> peers, int count)

    {

        if (_sortBuffer == null || _sortBuffer.Length < peers.Count)

            _sortBuffer = new (IPeerConnection, double, bool)[Math.Max(peers.Count, 16)];

        for (int i = 0; i < peers.Count; i++)

            _sortBuffer[i] = (peers[i], _statisticsTracker.GetPeerDownloadRate(peers[i]), IsPeerSnubbed(peers[i]));

        // Sort: non-snubbed first, then by rate descending

        Array.Sort(_sortBuffer, 0, peers.Count,

            Comparer<(IPeerConnection peer, double rate, bool snubbed)>.Create((a, b) =>

            {

                int cmp = a.snubbed.CompareTo(b.snubbed); // false < true, so non-snubbed first

                return cmp != 0 ? cmp : b.rate.CompareTo(a.rate);

            }));

        var result = new List<IPeerConnection>(Math.Min(count, peers.Count));

        for (int i = 0; i < peers.Count && result.Count < count; i++)

            result.Add(_sortBuffer[i].peer);

        return result;

    }

    /// <summary>

    /// Selects peers to unchoke while seeding.

    /// </summary>

    private List<IPeerConnection> SelectSeedingUnchokes(List<IPeerConnection> peers, int count)

    {

        switch (SeedAlgorithm)

        {

            case SeedChokingAlgorithm.RoundRobin:

                return SelectRoundRobinUnchokes(peers, count);

            case SeedChokingAlgorithm.AntiLeech:

                return SelectAntiLeechUnchokes(peers, count);

            case SeedChokingAlgorithm.FastestUpload:

            default:

                return SelectFastestUploadUnchokes(peers, count);

        }

    }

    /// <summary>

    /// Fastest upload algorithm - prefer peers we can upload to fastest.

    /// </summary>

    private List<IPeerConnection> SelectFastestUploadUnchokes(List<IPeerConnection> peers, int count)

    {

        if (_sortBuffer == null || _sortBuffer.Length < peers.Count)

            _sortBuffer = new (IPeerConnection, double, bool)[Math.Max(peers.Count, 16)];

        for (int i = 0; i < peers.Count; i++)

            _sortBuffer[i] = (peers[i], _statisticsTracker.GetPeerUploadRate(peers[i]), false);

        Array.Sort(_sortBuffer, 0, peers.Count,

            Comparer<(IPeerConnection, double, bool)>.Create((a, b) => b.Item2.CompareTo(a.Item2)));

        var result = new List<IPeerConnection>(Math.Min(count, peers.Count));

        for (int i = 0; i < peers.Count && result.Count < count; i++)

            result.Add(_sortBuffer[i].peer);

        return result;

    }

    /// <summary>

    /// Round-robin algorithm - rotate fairly among peers.

    /// Peers who have received their quota are deprioritized.

    /// </summary>

    private List<IPeerConnection> SelectRoundRobinUnchokes(List<IPeerConnection> peers, int count)

    {

        // Prefer peers who haven't been unchoked recently

        lock (_chokeLock)

        {

            if (_sortBuffer == null || _sortBuffer.Length < peers.Count)
                _sortBuffer = new (IPeerConnection peer, double rate, bool snubbed)[peers.Count];

            var now = DateTime.UtcNow;
            for (int i = 0; i < peers.Count; i++)
            {
                var p = peers[i];
                var isUnchoked = _currentlyUnchoked.Contains(p);
                var connectionSeconds = (now - p.ConnectedAt).TotalSeconds;
                // Sort key: choked peers first (0.0 < 1e9), then by longest connection (most negative)
                var sortKey = (isUnchoked ? 1_000_000_000.0 : 0.0) - connectionSeconds;
                _sortBuffer[i] = (p, sortKey, false);
            }

            var span = _sortBuffer.AsSpan(0, peers.Count);
            span.Sort((a, b) => a.rate.CompareTo(b.rate));

            int take = Math.Min(count, peers.Count);
            var result = new List<IPeerConnection>(take);
            for (int i = 0; i < take; i++)
                result.Add(span[i].peer);

            return result;

        }

    }

    /// <summary>

    /// Anti-leech algorithm from "Improving BitTorrent: A Simple Approach".

    /// Prefers peers who just started (need help) or are almost done (will become seeders).

    /// </summary>

    private List<IPeerConnection> SelectAntiLeechUnchokes(List<IPeerConnection> peers, int count)

    {

        if (_getTotalPiecesFunc == null || _getPeerPieceCountFunc == null)

        {

            // Fallback to fastest upload if we can't calculate piece counts

            return SelectFastestUploadUnchokes(peers, count);

        }

        int totalPieces = _getTotalPiecesFunc();

        if (totalPieces == 0) return SelectFastestUploadUnchokes(peers, count);

        return peers

            .Select(p =>

            {

                int peerPieces = _getPeerPieceCountFunc(p);

                // Anti-leech score: distance from 50% completion

                // Higher score for 0% (just started) and 100% (almost seeder)

                double completionRatio = (double)peerPieces / totalPieces;

                double antiLeechScore = Math.Abs(completionRatio - 0.5) * 2;

                return new

                {

                    Peer = p,

                    Score = antiLeechScore,

                    UploadRate = _statisticsTracker.GetPeerUploadRate(p)

                };

            })

            .OrderByDescending(x => x.Score)

            .ThenByDescending(x => x.UploadRate)

            .Select(x => x.Peer)

            .Take(count)

            .ToList();

    }

    /// <summary>
    /// Calculates adaptive upload slots: rate-based base with +1/-1 adjustments.
    /// </summary>
    private int CalculateAdaptiveSlots(List<IPeerConnection> interestedPeers)
    {
        // Start with rate-based slot count as base
        int baseSlots = CalculateRateBasedSlots(interestedPeers);

        // Bonus: +1 if top peer's rate > 2x mean
        double maxRate = 0, sumRate = 0;
        foreach (var peer in interestedPeers)
        {
            var rate = _statisticsTracker.GetPeerDownloadRate(peer);
            if (rate > maxRate) maxRate = rate;
            sumRate += rate;
        }
        double meanRate = interestedPeers.Count > 0 ? sumRate / interestedPeers.Count : 0;
        if (maxRate > 2 * meanRate && meanRate > 0) baseSlots++;

        // Penalty: -1 if >50% of unchoked are snubbed
        int unchokedCount, snubbedUnchoked = 0;
        lock (_chokeLock)
        {
            unchokedCount = _currentlyUnchoked.Count;
            foreach (var p in _currentlyUnchoked)
                if (_snubbedPeers.Contains(p)) snubbedUnchoked++;
        }
        if (unchokedCount > 0 && (double)snubbedUnchoked / unchokedCount > 0.5)
            baseSlots--;

        return Math.Clamp(baseSlots, _minUploadSlots, _maxUploadSlots);
    }

    /// <summary>
    /// Extracts the rate-based slot calculation for reuse by Adaptive algorithm.
    /// </summary>
    private int CalculateRateBasedSlots(List<IPeerConnection> interestedPeers)
    {
        if (_sortBuffer == null || _sortBuffer.Length < interestedPeers.Count)
            _sortBuffer = new (IPeerConnection, double, bool)[Math.Max(interestedPeers.Count, 16)];

        for (int i = 0; i < interestedPeers.Count; i++)
            _sortBuffer[i] = (interestedPeers[i], _statisticsTracker.GetPeerUploadRate(interestedPeers[i]), false);

        Array.Sort(_sortBuffer, 0, interestedPeers.Count,
            Comparer<(IPeerConnection, double, bool)>.Create((a, b) => b.Item2.CompareTo(a.Item2)));

        int slots = 0;
        int threshold = _rateThresholdInitial;
        for (int i = 0; i < interestedPeers.Count; i++)
        {
            var rate = _sortBuffer[i].rate;
            if (rate < threshold)
                break;
            slots++;
            threshold += _rateThresholdIncrement;
        }

        slots = Math.Max(slots + 1, _minUploadSlots);
        slots = Math.Min(slots, _maxUploadSlots);
        return slots;
    }

    /// <summary>
    /// Selects peers to unchoke using the adaptive 5-signal composite scoring algorithm.
    /// </summary>
    private List<IPeerConnection> SelectAdaptiveUnchokes(List<IPeerConnection> peers, int count)
    {
        bool seeding = _isSeedingFunc();
        int totalPieces = _getTotalPiecesFunc?.Invoke() ?? 1;

        // Estimate completion ratio from statistics
        int completedPieces = _statisticsTracker.PiecesCompleted;
        double completionRatio = seeding ? 1.0 : (totalPieces > 0 ? (double)completedPieces / totalPieces : 0.5);
        bool isEndgame = completionRatio > 0.95 && !seeding; // approximate endgame detection

        var phase = PeerScoreTracker.DetectPhase(completionRatio, seeding, isEndgame);

        var scores = _scoreTracker.ComputeScores(
            peers, phase, pexEnabled: true,
            getDownloadRate: p => _statisticsTracker.GetPeerDownloadRate(p),
            getPieceCount: p => _getPeerPieceCountFunc?.Invoke(p) ?? 0,
            getRttMs: p => p.RoundTripTimeMs,
            getSecsSinceLastData: p =>
            {
                lock (_chokeLock)
                {
                    if (_lastDataReceived.TryGetValue(p, out var last))
                        return (DateTime.UtcNow - last).TotalSeconds;
                }
                return _snubbedTimeout.TotalSeconds; // assume stale if no record
            },
            snubbedTimeoutSecs: _snubbedTimeout.TotalSeconds);

        // Sort by score descending, take top N
        var sorted = new List<(IPeerConnection peer, double score)>(peers.Count);
        foreach (var peer in peers)
        {
            scores.TryGetValue(peer, out var score);
            sorted.Add((peer, score));
        }
        sorted.Sort((a, b) => b.score.CompareTo(a.score));

        var result = new List<IPeerConnection>(Math.Min(count, sorted.Count));
        for (int i = 0; i < Math.Min(count, sorted.Count); i++)
            result.Add(sorted[i].peer);

        return result;
    }

    private void RotateOptimisticUnchoke(List<IPeerConnection> interestedPeers, List<IPeerConnection> regularUnchokes)

    {

        var now = DateTime.UtcNow;

        // Keep current optimistic peer if still valid and not time to rotate

        if (_optimisticPeer != null &&

            _optimisticPeer.IsConnected &&

            _interestedPeers.Contains(_optimisticPeer) &&

            now - _lastOptimisticRotation < _optimisticRotationInterval)

        {

            return;

        }

        // Build pool: use interestedPeers if non-empty, else snapshot connected peers

        List<IPeerConnection> pool;

        if (interestedPeers.Count > 0)

        {

            pool = interestedPeers;

        }

        else

        {

            pool = new List<IPeerConnection>();

            foreach (var p in _peerManager.ConnectedPeers)

                pool.Add(p);

        }

        // Build regularUnchokes lookup for O(1) checks

        var regularSet = new HashSet<IPeerConnection>(regularUnchokes);

        // Candidates are connected peers not in regular unchoke list and not current optimistic

        var candidates = new List<IPeerConnection>();

        foreach (var p in pool)

        {

            if (p.IsConnected && !regularSet.Contains(p) && p != _optimisticPeer)

                candidates.Add(p);

        }

        // Prefer newly connected peers (haven't been unchoked yet)

        var newPeers = new List<IPeerConnection>();

        foreach (var p in candidates)

        {

            if (!_currentlyUnchoked.Contains(p))

                newPeers.Add(p);

        }

        var candidatePool = newPeers.Count > 0 ? newPeers : candidates;

        if (candidatePool.Count > 0)

        {

            _optimisticPeer = candidatePool[Random.Shared.Next(candidatePool.Count)];

            _lastOptimisticRotation = now;

            LogOptimisticUnchoke(_logger, _optimisticPeer.PeerInfo.EndPoint, candidatePool.Count);

        }

        else

        {

            _optimisticPeer = null;

        }

    }

    private async Task ApplyChokingDecisionsAsync(List<IPeerConnection> allPeers, HashSet<IPeerConnection> shouldBeUnchoked)

    {

        var toChoke = new List<Task>();

        var toUnchoke = new List<Task>();

        lock (_chokeLock)

        {

            foreach (var peer in allPeers)

            {

                if (!peer.IsConnected) continue;

                bool isUnchoked = _currentlyUnchoked.Contains(peer);

                bool shouldUnchoke = shouldBeUnchoked.Contains(peer);

                if (shouldUnchoke && !isUnchoked)

                {

                    if (_unchokeAllocator != null && !_unchokeAllocator.TryAcquireUnchokeSlot())
                    {
                        LogGlobalUnchokeCapReached(_logger, _unchokeAllocator.MaxGlobalUnchokeSlots);
                        break;
                    }

                    _currentlyUnchoked.Add(peer);

                    toUnchoke.Add(UnchokePeerAsync(peer));

                }

                else if (!shouldUnchoke && isUnchoked)

                {

                    _currentlyUnchoked.Remove(peer);

                    _unchokeAllocator?.ReleaseUnchokeSlot();

                    toChoke.Add(ChokePeerAsync(peer));

                }

            }

        }

        await Task.WhenAll(toChoke.Concat(toUnchoke)).ConfigureAwait(false);

        if (toChoke.Any() || toUnchoke.Any())

        {

            LogRechoke(_logger, toUnchoke.Count, toChoke.Count, _currentUploadSlots);

        }

    }

    private async Task ChokeAllAsync()

    {

        List<IPeerConnection> toChoke;

        lock (_chokeLock)

        {

            toChoke = new List<IPeerConnection>(_currentlyUnchoked);

            if (_unchokeAllocator != null)
            {
                foreach (var _ in toChoke)
                    _unchokeAllocator.ReleaseUnchokeSlot();
            }

            _currentlyUnchoked.Clear();

            _optimisticPeer = null;

        }

        var tasks = new Task[toChoke.Count];

        for (int i = 0; i < toChoke.Count; i++)

            tasks[i] = ChokePeerAsync(toChoke[i]);

        await Task.WhenAll(tasks).ConfigureAwait(false);

    }

    private async Task UnchokePeerAsync(IPeerConnection peer)

    {

        try

        {

            await peer.SetChokingAsync(false).ConfigureAwait(false);

            PeerUnchoked?.Invoke(this, new PeerChokeChangedEventArgs(peer, isChoked: false));

        }

        catch (Exception ex)

        {

            _logger.LogWarning(ex, "Failed to unchoke {Peer}", peer.PeerInfo.EndPoint);

        }

    }

    private async Task ChokePeerAsync(IPeerConnection peer)

    {

        try

        {

            await peer.SetChokingAsync(true).ConfigureAwait(false);

            PeerChoked?.Invoke(this, new PeerChokeChangedEventArgs(peer, isChoked: true));

        }

        catch (Exception ex)

        {

            _logger.LogWarning(ex, "Failed to choke {Peer}", peer.PeerInfo.EndPoint);

        }

    }

    private void OnPeerConnected(object sender, PeerConnectedEventArgs e)

    {

        _statisticsTracker.RegisterPeer(e.Peer);
        _scoreTracker.OnPeerConnected(e.Peer);

        lock (_chokeLock)

        {

            _lastDataReceived[e.Peer] = DateTime.UtcNow;

        }

    }

    private void OnPeerDisconnected(object sender, PeerDisconnectedEventArgs e)

    {

        var connection = _peerManager.ConnectedPeers

            .FirstOrDefault(p => p.PeerInfo?.EndPoint?.ToString() == e.PeerInfo.EndPoint?.ToString());

        if (connection != null)

        {

            _statisticsTracker.UnregisterPeer(connection);
            _scoreTracker.OnPeerDisconnected(connection);

            lock (_chokeLock)

            {

                if (_currentlyUnchoked.Remove(connection))
                    _unchokeAllocator?.ReleaseUnchokeSlot();

                _interestedPeers.Remove(connection);

                _snubbedPeers.Remove(connection);

                _lastDataReceived.Remove(connection);

                if (_optimisticPeer == connection)

                    _optimisticPeer = null;

            }

        }

    }

    public void Dispose()

    {

        if (_disposed) return;

        _disposed = true;

        _cts?.Cancel();

        _cts?.Dispose();

        _peerManager.PeerConnected -= OnPeerConnected;

        _peerManager.PeerDisconnected -= OnPeerDisconnected;

    }

    // --- Source-generated logging (zero allocation when level disabled) ---

    [LoggerMessage(Level = LogLevel.Debug, Message = "Peer {Peer} is no longer snubbed (sent data)")]
    private static partial void LogPeerNoLongerSnubbed(ILogger logger, object peer);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Peer {Peer} is interested ({Count} total)")]
    private static partial void LogPeerIsInterested(ILogger logger, object peer, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Choked {Peer} (not interested)")]
    private static partial void LogChokedNotInterested(ILogger logger, object peer);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Global unchoke cap reached ({Max}), skipping fast unchoke for {Peer}")]
    private static partial void LogGlobalUnchokeCapSkipFastUnchoke(ILogger logger, int max, object peer);

    [LoggerMessage(Level = LogLevel.Debug, Message = "FAST UNCHOKE: {Peer} ({Rate}) [Slots: {Count}/{Max}]")]
    private static partial void LogFastUnchoke(ILogger logger, object peer, string rate, int count, int max);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Peer {Peer} marked as snubbed (no data for {Timeout}s)")]
    private static partial void LogPeerMarkedSnubbed(ILogger logger, object peer, double timeout);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Peer {Peer} marked as snubbed (no data since connection)")]
    private static partial void LogPeerMarkedSnubbedSinceConnection(ILogger logger, object peer);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Optimistic unchoke: {Peer} ({Count} candidates)")]
    private static partial void LogOptimisticUnchoke(ILogger logger, object peer, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Global unchoke cap reached ({Max}), cannot unchoke more peers")]
    private static partial void LogGlobalUnchokeCapReached(ILogger logger, int max);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rechoke: unchoked {Unchoke}, choked {Choke}, total slots: {Slots}")]
    private static partial void LogRechoke(ILogger logger, int unchoke, int choke, int slots);

}
