using System;

using System.Collections.Generic;

using System.Linq;

using System.Threading;

using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using vTorrent.Core.Interfaces;

using vTorrent.Core.PeerCommunication.Models;

using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Engine;

namespace vTorrent.Core.Upload;

/// <summary>

/// Unified peer replacement combining timer-based slow peer dropping and trial-period probing.

///

/// Two complementary strategies:

/// 1. Periodic evaluation: drops worst-performing peers below rate thresholds

/// 2. Probing: connects to candidate peers, evaluates after trial period, keeps or drops

///

/// Mode transitions adapt both strategies:

/// - Normal: 5min evaluation, 2min probing, conservative thresholds

/// - Endgame: 30s evaluation, 1min probing, zero-rate threshold only

/// - Emergency: 10s evaluation, aggressive replacements

/// </summary>

public class PeerReplacer : IPeerProber

{

    private readonly IPeerManager _peerManager;

    private readonly IStatisticsTracker _statisticsTracker;

    private readonly ILogger<PeerReplacer> _logger;

    private readonly Func<bool> _isSeeding;

    private readonly IOptionsMonitor<BehaviorSettings>? _behaviorMonitor;

    // Timers

    private PeriodicTimer? _evaluationTimer;

    private PeriodicTimer? _probingTimer;

    private CancellationTokenSource? _cts;

    private Timer? _trialEvaluationTimer;

    private bool _isRunning;

    // Evaluation configuration (adaptive)

    private TimeSpan _evaluationInterval = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _normalEvaluationInterval = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _endgameEvaluationInterval = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _emergencyEvaluationInterval = TimeSpan.FromSeconds(10);

    private readonly int _minDownloadRateThreshold = 5 * 1024; // 5 KB/s

    private int _maxReplacementsPerCycle = 3;

    // Probing configuration (adaptive)

    private TimeSpan _probingInterval = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _trialPeriod = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _normalProbingInterval = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _endgameProbingInterval = TimeSpan.FromMinutes(1);

    // Probing state

    private IPeerConnection? _probingPeer;

    private DateTime _probingStartTime;

    private readonly Queue<PeerInfo> _candidatePeers = new();

    private readonly object _candidateLock = new();

    // Mode flags

    private bool _isEndgameMode;

    private bool _isEmergencyMode;

    // Statistics

    private int _totalDropped;

    private int _totalProbed;

    private int _totalKept;

    public bool IsEnabled { get; set; } = true;

    public int TotalDropped => _totalDropped;

    public int TotalProbed => _totalProbed;

    public int TotalKept => _totalKept;

    public double SuccessRate => _totalProbed > 0 ? (double)_totalKept / _totalProbed : 0;

    public PeerReplacer(

        IPeerManager peerManager,

        IStatisticsTracker statisticsTracker,

        Func<bool> isSeeding,

        ILogger<PeerReplacer> logger,

        IOptionsMonitor<BehaviorSettings>? behaviorMonitor = null)

    {

        _peerManager = peerManager ?? throw new ArgumentNullException(nameof(peerManager));

        _statisticsTracker = statisticsTracker ?? throw new ArgumentNullException(nameof(statisticsTracker));

        _isSeeding = isSeeding ?? throw new ArgumentNullException(nameof(isSeeding));

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _behaviorMonitor = behaviorMonitor;

    }

    public Task StartAsync()

    {

        if (_isRunning)

            return Task.CompletedTask;

        _isRunning = true;

        _cts = new CancellationTokenSource();

        _evaluationTimer = new PeriodicTimer(_evaluationInterval);

        _probingTimer = new PeriodicTimer(_probingInterval);

        _ = RunEvaluationLoopAsync(_cts.Token);

        _ = RunProbingLoopAsync(_cts.Token);

        _logger.LogDebug(

            "PeerReplacer started (Eval: {EvalMin}min, Probe: {ProbeMin}min, Threshold: {Threshold}KB/s)",

            _evaluationInterval.TotalMinutes, _probingInterval.TotalMinutes, _minDownloadRateThreshold / 1024);

        return Task.CompletedTask;

    }

    private async Task RunEvaluationLoopAsync(CancellationToken ct)

    {

        try

        {

            while (await _evaluationTimer!.WaitForNextTickAsync(ct).ConfigureAwait(false))

            {

                await EvaluateSlowPeersAsync().ConfigureAwait(false);

            }

        }

        catch (OperationCanceledException) { }

    }

    private async Task RunProbingLoopAsync(CancellationToken ct)

    {

        try

        {

            while (await _probingTimer!.WaitForNextTickAsync(ct).ConfigureAwait(false))

            {

                await ProbeNextPeerAsync().ConfigureAwait(false);

            }

        }

        catch (OperationCanceledException) { }

    }

    public Task StopAsync()

    {

        if (!_isRunning)

            return Task.CompletedTask;

        _isRunning = false;

        _cts?.Cancel();

        _cts?.Dispose();

        _cts = null;

        _evaluationTimer?.Dispose();

        _evaluationTimer = null;

        _probingTimer?.Dispose();

        _probingTimer = null;

        _trialEvaluationTimer?.Dispose();

        _trialEvaluationTimer = null;

        _logger.LogDebug(

            "PeerReplacer stopped (Dropped: {Dropped}, Probed: {Probed}, Kept: {Kept}, Success: {Success:P1})",

            _totalDropped, _totalProbed, _totalKept, SuccessRate);

        return Task.CompletedTask;

    }

    #region Candidate management

    public void AddCandidatePeers(IEnumerable<PeerInfo> peers)

    {

        lock (_candidateLock)

        {

            foreach (var peer in peers)

            {

                if (_peerManager.ConnectedPeers.Any(p =>

                    p.PeerInfo.IpAddress.Equals(peer.IpAddress) && p.PeerInfo.Port == peer.Port))

                    continue;

                _candidatePeers.Enqueue(peer);

            }

            _logger.LogDebug("Candidate queue size: {QueueSize}", _candidatePeers.Count);

        }

    }

    #endregion

    #region Mode transitions

    public void EnterEndgameMode()

    {

        if (_isEndgameMode)

            return;

        _isEndgameMode = true;

        _evaluationInterval = _endgameEvaluationInterval;

        _probingInterval = _endgameProbingInterval;

        _maxReplacementsPerCycle = 5;

        _logger.LogDebug(

            "PeerReplacer entering ENDGAME (Eval: {EvalSec}s, Probe: {ProbeMin}min, MaxReplace: {Max})",

            _evaluationInterval.TotalSeconds, _probingInterval.TotalMinutes, _maxReplacementsPerCycle);

        RestartTimers();

    }

    public void ExitEndgameMode()

    {

        if (!_isEndgameMode && !_isEmergencyMode)

            return;

        _isEndgameMode = false;

        _isEmergencyMode = false;

        _evaluationInterval = _normalEvaluationInterval;

        _probingInterval = _normalProbingInterval;

        _maxReplacementsPerCycle = 3;

        _logger.LogDebug("PeerReplacer returning to NORMAL mode");

        RestartTimers();

    }

    public void EnterEmergencyMode()

    {

        if (_isEmergencyMode)

            return;

        _isEmergencyMode = true;

        _isEndgameMode = true;

        _evaluationInterval = _emergencyEvaluationInterval;

        _maxReplacementsPerCycle = 10;

        _logger.LogWarning(

            "PeerReplacer entering EMERGENCY (Eval: {EvalSec}s, MaxReplace: {Max})",

            _evaluationInterval.TotalSeconds, _maxReplacementsPerCycle);

        RestartTimers();

    }

    private void RestartTimers()

    {

        if (!_isRunning)

            return;

        // Cancel existing loops

        _cts?.Cancel();

        _cts?.Dispose();

        _evaluationTimer?.Dispose();

        _probingTimer?.Dispose();

        // Start fresh

        _cts = new CancellationTokenSource();

        _evaluationTimer = new PeriodicTimer(_evaluationInterval);

        _probingTimer = new PeriodicTimer(_probingInterval);

        _ = RunEvaluationLoopAsync(_cts.Token);

        _ = RunProbingLoopAsync(_cts.Token);

    }

    #endregion

    #region Slow peer evaluation (from SimplePeerReplacer)

    private async Task EvaluateSlowPeersAsync()

    {

        if (!IsEnabled || !_isRunning)

            return;

        // Don't evaluate during seeding - all peers have 0 download rate

        if (_isSeeding())

        {

            _logger.LogDebug("Skipping peer evaluation during seeding");

            return;

        }

        // Don't drop peers during endgame — duplicate block delivery causes

        // artificially low payload rates. Dropping these peers cascades to

        // total peer loss and 99% stall. Let the download coordinator manage

        // endgame peer selection via duplicate block requests.

        if (_isEndgameMode)

        {

            _logger.LogDebug("Skipping peer evaluation during endgame (duplicate blocks suppress payload rate)");

            return;

        }

        // Apply Normal-mode settings from BehaviorSettings
        if (_behaviorMonitor != null)
        {
            var behavior = _behaviorMonitor.CurrentValue;
            // PeerTurnoverInterval overrides normal evaluation interval
            _evaluationInterval = TimeSpan.FromSeconds(behavior.PeerTurnoverInterval);
            // PeerTurnover percentage determines max replacements
            var connectedCount = _peerManager.ConnectedPeers.Count;
            _maxReplacementsPerCycle = Math.Max(1, connectedCount * behavior.PeerTurnover / 100);
        }

        try

        {

            var connectedPeers = _peerManager.ConnectedPeers;

            if (connectedPeers.Count == 0)

                return;

            // Only trigger turnover when at > cutoff% of peer limit
            var behaviorForCutoff = _behaviorMonitor?.CurrentValue;
            var cutoff = behaviorForCutoff?.PeerTurnoverCutoff ?? 90;
            // TODO: expose MaxConnections on IPeerManager if needed; using ConnectedPeerCount vs MaxConnections
            if (connectedPeers.Count < _peerManager.MaxConnections * cutoff / 100)
            {
                _logger.LogDebug("Skipping peer turnover: {Connected}/{Max} peers below cutoff {Cutoff}%",
                    connectedPeers.Count, _peerManager.MaxConnections, cutoff);
                return;
            }

            var rateThreshold = _minDownloadRateThreshold;

            var slowPeers = connectedPeers

                .Select(p => new { Peer = p, Rate = _statisticsTracker.GetPeerDownloadRate(p) })

                .Where(x => x.Rate <= rateThreshold)

                .OrderBy(x => x.Rate)

                .Take(_maxReplacementsPerCycle)

                .ToList();

            if (slowPeers.Count == 0)

            {

                _logger.LogDebug("All peers performing adequately (>{Threshold} KB/s)",

                    _minDownloadRateThreshold / 1024);

                return;

            }

            foreach (var item in slowPeers)

            {

                _logger.LogDebug("Dropping slow peer {Peer} (Rate: {Rate})",

                    item.Peer.PeerInfo.EndPoint,

                    TorrentUtilities.FormatRate(item.Rate));

                await _peerManager.RemovePeerAsync(item.Peer).ConfigureAwait(false);

                _totalDropped++;

            }

            _logger.LogDebug("Dropped {Count} slow peers (Total: {Total})",

                slowPeers.Count, _totalDropped);

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Error during slow peer evaluation");

        }

    }

    #endregion

    #region Probing (from AdaptivePeerProber)

    private async Task ProbeNextPeerAsync()

    {

        if (!IsEnabled || !_isRunning)

            return;

        try

        {

            if (_probingPeer != null)

            {

                _logger.LogDebug("Already probing a peer, skipping this cycle");

                return;

            }

            if (_peerManager.ConnectedPeerCount >= _peerManager.MaxConnections)

            {

                _logger.LogDebug("At max connections ({Count}), cannot probe", _peerManager.MaxConnections);

                return;

            }

            PeerInfo? candidatePeer = null;

            lock (_candidateLock)

            {

                if (_candidatePeers.Count == 0)

                    return;

                candidatePeer = _candidatePeers.Dequeue();

            }

            if (candidatePeer == null)

                return;

            _logger.LogDebug("Probing new peer: {Peer}",

                $"{candidatePeer.IpAddress}:{candidatePeer.Port}");

            var connected = await _peerManager.AddPeerAsync(candidatePeer).ConfigureAwait(false);

            if (connected)

            {

                _probingPeer = _peerManager.ConnectedPeers

                    .FirstOrDefault(p => p.PeerInfo.IpAddress.Equals(candidatePeer.IpAddress)

                                         && p.PeerInfo.Port == candidatePeer.Port);

                if (_probingPeer != null)

                {

                    _probingStartTime = DateTime.UtcNow;

                    _totalProbed++;

                    _trialEvaluationTimer?.Dispose();

                    _trialEvaluationTimer = new Timer(

                        async _ => await EvaluateProbingPeerAsync().ConfigureAwait(false),

                        null, _trialPeriod, Timeout.InfiniteTimeSpan);

                    _logger.LogDebug("Trial period started ({Seconds}s)", _trialPeriod.TotalSeconds);

                }

            }

            else

            {

                _logger.LogDebug("Failed to connect to probing peer {Peer}",

                    $"{candidatePeer.IpAddress}:{candidatePeer.Port}");

            }

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Error probing next peer");

        }

    }

    private async Task EvaluateProbingPeerAsync()

    {

        if (_probingPeer == null || !_isRunning)

            return;

        try

        {

            var trialDuration = DateTime.UtcNow - _probingStartTime;

            if (!_probingPeer.IsConnected)

            {

                _logger.LogDebug("Probing peer disconnected during trial ({Duration:F1}s)",

                    trialDuration.TotalSeconds);

                _probingPeer = null;

                _totalDropped++;

                return;

            }

            var probingRate = _statisticsTracker.GetPeerDownloadRate(_probingPeer);

            _logger.LogDebug("Evaluating probing peer {Peer} after {Duration:F1}s (Rate: {Rate})",

                _probingPeer.PeerInfo.EndPoint,

                trialDuration.TotalSeconds,

                TorrentUtilities.FormatRate(probingRate));

            var existingPeers = _peerManager.ConnectedPeers

                .Where(p => p != _probingPeer)

                .Select(p => new { Peer = p, Rate = _statisticsTracker.GetPeerDownloadRate(p) })

                .OrderBy(x => x.Rate)

                .ToList();

            if (existingPeers.Count == 0)

            {

                _logger.LogDebug("Probing peer is the only peer, keeping");

                _probingPeer = null;

                _totalKept++;

                return;

            }

            var slowest = existingPeers.First();

            var median = existingPeers[existingPeers.Count / 2];

            if (probingRate < slowest.Rate)

            {

                // Slower than slowest - drop probing peer

                _logger.LogDebug("Probing peer FAILED trial ({Rate} < slowest {Slowest}), removing",

                    TorrentUtilities.FormatRate(probingRate), TorrentUtilities.FormatRate(slowest.Rate));

                await _peerManager.RemovePeerAsync(_probingPeer).ConfigureAwait(false);

                _totalDropped++;

            }

            else if (probingRate < median.Rate)

            {

                // Between slowest and median - keep probing peer, remove slowest

                _logger.LogDebug("Probing peer PASSED trial ({Rate} > slowest {Slowest}), replacing slowest",

                    TorrentUtilities.FormatRate(probingRate), TorrentUtilities.FormatRate(slowest.Rate));

                await _peerManager.RemovePeerAsync(slowest.Peer).ConfigureAwait(false);

                _totalKept++;

            }

            else

            {

                // Faster than median - keep probing peer

                _logger.LogDebug("Probing peer EXCELLED ({Rate} > median {Median}), keeping",

                    TorrentUtilities.FormatRate(probingRate), TorrentUtilities.FormatRate(median.Rate));

                if (_peerManager.ConnectedPeerCount >= _peerManager.MaxConnections)

                {

                    _logger.LogDebug("At max connections, removing slowest to make room");

                    await _peerManager.RemovePeerAsync(slowest.Peer).ConfigureAwait(false);

                }

                _totalKept++;

            }

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Error evaluating probing peer");

        }

        finally

        {

            _probingPeer = null;

            _trialEvaluationTimer?.Dispose();

            _trialEvaluationTimer = null;

        }

    }

    #endregion

    public void Dispose()

    {

        _isRunning = false;

        _cts?.Cancel();

        _cts?.Dispose();

        _cts = null;

        _evaluationTimer?.Dispose();

        _evaluationTimer = null;

        _probingTimer?.Dispose();

        _probingTimer = null;

        _trialEvaluationTimer?.Dispose();

        _trialEvaluationTimer = null;

    }

}
