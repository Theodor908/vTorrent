using System;

using System.Collections.Concurrent;

using System.Collections.Generic;

using System.Linq;

using System.Threading;

using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using vTorrent.Core.Interfaces;

using vTorrent.Core.PeerCommunication.Events;

using vTorrent.Core.PeerCommunication.Models;

using vTorrent.Abstractions.Models;

using vTorrent.Abstractions.Enums;
using vTorrent.Core.Engine;

namespace vTorrent.Core.PeerCommunication.Models;

public class PeerConnectionManager : IDisposable

{

    private readonly ILogger<PeerConnectionManager> _logger;

    private readonly IPeerManager _peerManager;

    private readonly PeerRegistry _peerRegistry;

    private readonly IStatisticsTracker _statisticsTracker;

    private readonly PeerConnectionSettings _settings;

    // Connection queue

    private readonly ConcurrentQueue<PeerCandidate> _connectionQueue = new();

    private readonly HashSet<string> _pendingConnections = new();

    private readonly object _connectionLock = new();

    // Background tasks

    private CancellationTokenSource _cts;

    private Task _maintenanceTask;

    private Task _connectionTask;

    private bool _disposed;

    // Events

    public event EventHandler<PeerPrioritizedEventArgs> PeerPrioritized;

    public int KnownPeerCount => _peerRegistry.TotalPeerCount;

    public int BannedPeerCount => _peerRegistry.GetAllByStatus(PeerConnectionStatus.Banned).Count;

    public int QueuedConnectionCount => _connectionQueue.Count;

    public PeerConnectionManager(

        IPeerManager peerManager,

        PeerRegistry peerRegistry,

        IStatisticsTracker statisticsTracker,

        PeerConnectionSettings settings,

        ILogger<PeerConnectionManager> logger)

    {

        _peerManager = peerManager ?? throw new ArgumentNullException(nameof(peerManager));

        _peerRegistry = peerRegistry ?? throw new ArgumentNullException(nameof(peerRegistry));

        _statisticsTracker = statisticsTracker ?? throw new ArgumentNullException(nameof(statisticsTracker));

        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _peerManager.PeerConnected += OnPeerConnected;

        _peerManager.PeerDisconnected += OnPeerDisconnected;

        _logger.LogDebug("PeerConnectionManager initialized - Target: {Min}-{Max} peers",

            _settings.MinConnections, _settings.MaxConnections);

    }

    public Task StartAsync(CancellationToken cancellationToken = default)

    {

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _maintenanceTask = Task.Run(() => MaintenanceLoopAsync(_cts.Token), _cts.Token);

        _connectionTask = Task.Run(() => ConnectionLoopAsync(_cts.Token), _cts.Token);

        _logger.LogDebug("PeerConnectionManager started");

        return Task.CompletedTask;

    }

    public async Task StopAsync()

    {

        _cts?.Cancel();

        var tasks = new List<Task>();

        if (_maintenanceTask != null) tasks.Add(_maintenanceTask);

        if (_connectionTask != null) tasks.Add(_connectionTask);

        try

        {

            await Task.WhenAll(tasks).ConfigureAwait(false);

        }

        catch (OperationCanceledException) { }

        _logger.LogDebug("PeerConnectionManager stopped");

    }

    /// <summary>

    /// Add a discovered peer to the connection queue.

    /// </summary>

    public void AddDiscoveredPeer(PeerInfo peerInfo, string source = "tracker")

    {

        if (peerInfo == null) return;

        var key = PeerRegistry.GetPeerKey(peerInfo);

        // Skip if banned

        if (_peerRegistry.IsBanned(key))

        {

            _logger.LogTrace("Skipping banned peer {Peer}", peerInfo.EndPoint);

            return;

        }

        // Skip if already connected

        if (_peerManager.IsConnected(peerInfo))

        {

            return;

        }

        // Get or register peer state

        var peerState = _peerRegistry.GetOrRegister(peerInfo);

        // Peer list is full — skip this peer
        if (peerState == null)
        {
            return;
        }

        peerState.Score.LastSeen = DateTime.UtcNow;

        peerState.Score.Source = source;

        // Add to connection queue if not already pending

        lock (_connectionLock)

        {

            if (!_pendingConnections.Contains(key))

            {

                peerState.Score.UpdatePriority();

                _connectionQueue.Enqueue(new PeerCandidate(peerInfo, peerState.Score.Priority));

                _pendingConnections.Add(key);

                _logger.LogTrace("Queued peer {Peer} (priority: {Priority})",

                    peerInfo.EndPoint, peerState.Score.Priority);

            }

        }

    }

    /// <summary>

    /// Add multiple discovered peers.

    /// </summary>

    public void AddDiscoveredPeers(IEnumerable<PeerInfo> peers, string source = "tracker")

    {

        foreach (var peer in peers)

        {

            AddDiscoveredPeer(peer, source);

        }

    }

    /// <summary>

    /// Ban a peer for misbehavior.

    /// </summary>

    public void BanPeer(PeerInfo peerInfo, TimeSpan duration, string reason)

    {

        var key = PeerRegistry.GetPeerKey(peerInfo);

        _peerRegistry.Ban(key, duration, reason);

        _logger.LogWarning("Banned peer {Peer} for {Duration}: {Reason}",

            peerInfo.EndPoint, duration, reason);

    }

    /// <summary>

    /// Report a peer protocol violation.

    /// </summary>

    public void ReportViolation(IPeerConnection peer, string violation)

    {

        var key = PeerRegistry.GetPeerKey(peer.PeerInfo);

        if (_peerRegistry.TryGetPeer(key, out var peerState))

        {

            peerState.Score.ProtocolViolations++;

            // Auto-ban after too many violations

            if (peerState.Score.ProtocolViolations >= _settings.MaxViolationsBeforeBan)

            {

                BanPeer(peer.PeerInfo, _settings.ViolationBanDuration, $"Too many violations: {violation}");

            }

        }

    }

    /// <summary>

    /// Get prioritized list of peers to connect to.

    /// </summary>

    public IReadOnlyList<PeerInfo> GetPrioritizedPeers(int count)

    {

        return _peerRegistry.GetPeersWhere(ps =>

                ps.Status != PeerConnectionStatus.Connected &&

                ps.Status != PeerConnectionStatus.Banned &&

                !_peerManager.IsConnected(ps.Info))

            .OrderByDescending(ps => ps.Score.Priority)

            .Take(count)

            .Select(ps => ps.Info)

            .ToList();

    }

    private async Task MaintenanceLoopAsync(CancellationToken ct)

    {

        while (!ct.IsCancellationRequested)

        {

            try

            {

                await Task.Delay(_settings.MaintenanceInterval, ct).ConfigureAwait(false);

                // Update peer scores based on current statistics

                UpdatePeerScores();

                // Clean up expired bans

                CleanupExpiredBans();

                // Check if we need more connections

                await MaintainConnectionCountAsync(ct).ConfigureAwait(false);

                // Prune old peer scores

                PruneOldPeers();

                // Log status

                LogConnectionStatus();

            }

            catch (OperationCanceledException) { break; }

            catch (Exception ex)

            {

                _logger.LogError(ex, "Error in maintenance loop");

            }

        }

    }

    private async Task ConnectionLoopAsync(CancellationToken ct)

    {

        while (!ct.IsCancellationRequested)

        {

            try

            {

                // Check if we should connect more peers

                if (_peerManager.ConnectedPeerCount >= _settings.MaxConnections)

                {

                    await Task.Delay(1000, ct).ConfigureAwait(false);

                    continue;

                }

                // Try to dequeue a peer

                if (!_connectionQueue.TryDequeue(out var candidate))

                {

                    await Task.Delay(500, ct).ConfigureAwait(false);

                    continue;

                }

                var key = PeerRegistry.GetPeerKey(candidate.PeerInfo);

                lock (_connectionLock)

                {

                    _pendingConnections.Remove(key);

                }

                // Skip if banned or already connected

                if (_peerRegistry.IsBanned(key) || _peerManager.IsConnected(candidate.PeerInfo))

                {

                    continue;

                }

                // Attempt connection

                _logger.LogDebug("Connecting to {Peer} (priority: {Priority})",

                    candidate.PeerInfo.EndPoint, candidate.Priority);

                var success = await _peerManager.AddPeerAsync(candidate.PeerInfo, ct).ConfigureAwait(false);

                if (_peerRegistry.TryGetPeer(key, out var peerState))

                {

                    peerState.Score.ConnectionAttempts++;

                    if (success)

                    {

                        peerState.Score.SuccessfulConnections++;

                        peerState.Score.LastConnected = DateTime.UtcNow;

                    }

                    else

                    {

                        peerState.Score.FailedConnections++;

                        peerState.Score.LastFailure = DateTime.UtcNow;

                    }

                }

                // Small delay between connection attempts

                await Task.Delay(_settings.ConnectionAttemptDelay, ct).ConfigureAwait(false);

            }

            catch (OperationCanceledException) { break; }

            catch (Exception ex)

            {

                _logger.LogError(ex, "Error in connection loop");

                await Task.Delay(1000, ct).ConfigureAwait(false);

            }

        }

    }

    private void UpdatePeerScores()

    {

        foreach (var peer in _peerManager.ConnectedPeers)

        {

            var key = PeerRegistry.GetPeerKey(peer.PeerInfo);

            if (_peerRegistry.TryGetPeer(key, out var peerState))

            {

                var score = peerState.Score;

                // Update transfer rates from statistics tracker

                score.DownloadRate = _statisticsTracker.GetPeerDownloadRate(peer);

                score.UploadRate = _statisticsTracker.GetPeerUploadRate(peer);

                score.TotalDownloaded = _statisticsTracker.GetPeerDownloaded(peer);

                score.TotalUploaded = _statisticsTracker.GetPeerUploaded(peer);

                // Update connection duration

                score.TotalConnectionTime += DateTime.UtcNow - (score.LastConnected ?? DateTime.UtcNow);

                // Check for stalled peer

                if (score.DownloadRate < _settings.MinUsefulRate &&

                    score.UploadRate < _settings.MinUsefulRate &&

                    score.TotalConnectionTime > _settings.StallDetectionTime)

                {

                    score.StallCount++;

                }

                // Recalculate priority

                score.UpdatePriority();

            }

        }

    }

    private async Task MaintainConnectionCountAsync(CancellationToken ct)

    {

        var currentCount = _peerManager.ConnectedPeerCount;

        // Need more connections?

        if (currentCount < _settings.MinConnections)

        {

            var needed = _settings.TargetConnections - currentCount;

            var candidates = GetPrioritizedPeers(needed * 2); // Request more than needed

            _logger.LogDebug("Below minimum connections ({Current}/{Min}), queuing {Count} peers",

                currentCount, _settings.MinConnections, candidates.Count);

            foreach (var peer in candidates)

            {

                AddDiscoveredPeer(peer, "maintenance");

            }

        }

        // Too many connections? Disconnect worst performers

        if (currentCount > _settings.MaxConnections)

        {

            var toDisconnect = currentCount - _settings.MaxConnections;

            var worstPeers = GetWorstPerformingPeers(toDisconnect);

            foreach (var peer in worstPeers)

            {

                _logger.LogDebug("Disconnecting low-priority peer {Peer}", peer.PeerInfo.EndPoint);

                await _peerManager.RemovePeerAsync(peer).ConfigureAwait(false);

            }

        }

        // Replace poor performers with better candidates if we have room

        if (currentCount >= _settings.MinConnections && currentCount < _settings.MaxConnections)

        {

            await ConsiderPeerReplacementAsync(ct).ConfigureAwait(false);

        }

    }

    private async Task ConsiderPeerReplacementAsync(CancellationToken ct)

    {

        // Find our worst connected peer

        var connectedPeers = _peerManager.ConnectedPeers.ToList();

        if (connectedPeers.Count == 0) return;

        var worstConnected = connectedPeers

            .Select(p =>

            {

                var key = PeerRegistry.GetPeerKey(p.PeerInfo);

                _peerRegistry.TryGetPeer(key, out var state);

                return new { Peer = p, State = state };

            })

            .Where(x => x.State != null)

            .OrderBy(x => x.State.Score.Priority)

            .FirstOrDefault();

        if (worstConnected == null) return;

        // Find best unconnected candidate

        var bestCandidate = _peerRegistry.GetPeersWhere(ps =>

                ps.Status != PeerConnectionStatus.Connected &&

                ps.Status != PeerConnectionStatus.Banned &&

                !_peerManager.IsConnected(ps.Info))

            .OrderByDescending(ps => ps.Score.Priority)

            .FirstOrDefault();

        if (bestCandidate == null) return;

        // Replace if candidate is significantly better

        if (bestCandidate.Score.Priority > worstConnected.State.Score.Priority * _settings.ReplacementThreshold)

        {

            _logger.LogDebug("Replacing peer {Old} (priority {OldPri:F2}) with {New} (priority {NewPri:F2})",

                worstConnected.Peer.PeerInfo.EndPoint, worstConnected.State.Score.Priority,

                bestCandidate.Info.EndPoint, bestCandidate.Score.Priority);

            await _peerManager.RemovePeerAsync(worstConnected.Peer).ConfigureAwait(false);

            AddDiscoveredPeer(bestCandidate.Info, "replacement");

        }

    }

    private IEnumerable<IPeerConnection> GetWorstPerformingPeers(int count)

    {

        return _peerManager.ConnectedPeers

            .Select(p =>

            {

                var key = PeerRegistry.GetPeerKey(p.PeerInfo);

                _peerRegistry.TryGetPeer(key, out var state);

                return new { Peer = p, State = state };

            })

            .Where(x => x.State != null)

            .OrderBy(x => x.State.Score.Priority)

            .Take(count)

            .Select(x => x.Peer);

    }

    private void CleanupExpiredBans()

    {

        // Ban expiry is now automatically handled by PeerRegistry.IsBanned()

        // No manual cleanup needed

    }

    private void PruneOldPeers()

    {

        var cutoff = DateTime.UtcNow - _settings.PeerRetentionTime;

        var oldPeers = _peerRegistry.GetPeersWhere(ps =>

                ps.Score.LastSeen < cutoff &&

                ps.Status != PeerConnectionStatus.Connected &&

                !_peerManager.IsConnected(ps.Info))

            .ToList();

        foreach (var peerState in oldPeers)

        {

            _peerRegistry.Remove(PeerRegistry.GetPeerKey(peerState.Info));

        }

        if (oldPeers.Count > 0)

        {

            _logger.LogDebug("Pruned {Count} old peer records", oldPeers.Count);

        }

    }

    private void OnPeerConnected(object sender, PeerConnectedEventArgs e)

    {

        var key = PeerRegistry.GetPeerKey(e.Peer.PeerInfo);

        if (_peerRegistry.TryGetPeer(key, out var peerState))

        {

            peerState.Score.LastConnected = DateTime.UtcNow;

            peerState.Score.CurrentlyConnected = true;

        }

    }

    private void OnPeerDisconnected(object sender, PeerDisconnectedEventArgs e)

    {

        var key = PeerRegistry.GetPeerKey(e.PeerInfo);

        if (_peerRegistry.TryGetPeer(key, out var peerState))

        {

            peerState.Score.CurrentlyConnected = false;

            peerState.Score.DisconnectionCount++;

            // Calculate reconnection delay based on peer quality

            var delay = CalculateReconnectionDelay(peerState.Score, e.Reason);

            if (delay < TimeSpan.MaxValue && !_peerRegistry.IsBanned(key))

            {

                _ = ScheduleReconnectionAsync(e.PeerInfo, delay);

            }

        }

    }

    private TimeSpan CalculateReconnectionDelay(PeerScore score, string reason)

    {

        // Don't reconnect to peers we removed intentionally

        if (reason == "Removed by manager")

            return TimeSpan.MaxValue;

        // Base delay

        var baseDelay = _settings.BaseReconnectionDelay;

        // Increase delay for repeated failures

        var failureMultiplier = Math.Pow(2, Math.Min(score.FailedConnections, 5));

        // Decrease delay for good performers

        var performanceMultiplier = score.Priority > 0.5 ? 0.5 : 1.0;

        // Increase delay for frequent disconnections

        var disconnectMultiplier = 1.0 + (score.DisconnectionCount * 0.2);

        var delay = TimeSpan.FromSeconds(

            baseDelay.TotalSeconds * failureMultiplier * performanceMultiplier * disconnectMultiplier);

        // Clamp to reasonable bounds

        if (delay < _settings.MinReconnectionDelay)

            delay = _settings.MinReconnectionDelay;

        if (delay > _settings.MaxReconnectionDelay)

            delay = _settings.MaxReconnectionDelay;

        return delay;

    }

    private async Task ScheduleReconnectionAsync(PeerInfo peerInfo, TimeSpan delay)

    {

        _logger.LogDebug("Scheduling reconnection to {Peer} in {Delay}", peerInfo.EndPoint, delay);

        await Task.Delay(delay).ConfigureAwait(false);

        if (!_disposed && !_peerRegistry.IsBanned(PeerRegistry.GetPeerKey(peerInfo)))

        {

            AddDiscoveredPeer(peerInfo, "reconnection");

        }

    }

    private void LogConnectionStatus()

    {

        var connected = _peerManager.ConnectedPeerCount;

        var queued = _connectionQueue.Count;

        var banned = _peerRegistry.GetAllByStatus(PeerConnectionStatus.Banned).Count;

        var known = _peerRegistry.TotalPeerCount;

        _logger.LogDebug("Connections: {Connected}/{Max} | Queued: {Queued} | Known: {Known} | Banned: {Banned}",

            connected, _settings.MaxConnections, queued, known, banned);

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

}

/// <summary>

/// Peer candidate for connection queue.

/// </summary>

public readonly record struct PeerCandidate(PeerInfo PeerInfo, double Priority);

/// <summary>

/// Event args for peer prioritization changes.

/// </summary>

public class PeerPrioritizedEventArgs : EventArgs

{

    public PeerInfo PeerInfo { get; }

    public double OldPriority { get; }

    public double NewPriority { get; }

    public PeerPrioritizedEventArgs(PeerInfo peerInfo, double oldPriority, double newPriority)

    {

        PeerInfo = peerInfo;

        OldPriority = oldPriority;

        NewPriority = newPriority;

    }

}

/// <summary>

/// Settings for peer connection management.

/// </summary>

public class PeerConnectionSettings

{

    public int MinConnections { get; set; } = 50;

    public int TargetConnections { get; set; } = 150;

    public int MaxConnections { get; set; } = 200;

    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ConnectionAttemptDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan BaseReconnectionDelay { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan MinReconnectionDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxReconnectionDelay { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan PeerRetentionTime { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan StallDetectionTime { get; set; } = TimeSpan.FromMinutes(2);

    public double MinUsefulRate { get; set; } = 1024; // 1 KB/s

    public double ReplacementThreshold { get; set; } = 1.5; // New peer must be 50% better

    public int MaxViolationsBeforeBan { get; set; } = 3;

    public TimeSpan ViolationBanDuration { get; set; } = TimeSpan.FromHours(1);

}