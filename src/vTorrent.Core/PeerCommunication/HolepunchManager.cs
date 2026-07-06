using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Core.PeerCommunication.Extensions;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Transport.Utp;

namespace vTorrent.Core.PeerCommunication;

/// <summary>
/// Implements the three-role BEP 55 NAT holepunch state machine:
///   Initiator  — sends Rendezvous to relay, waits for Connect, then connects via uTP
///   Relay      — receives Rendezvous, sends Connect to both sides
///   Target     — receives Connect, initiates uTP to the initiator
/// </summary>
public sealed class HolepunchManager : IDisposable
{
    // -------------------------------------------------------------------------
    // Internal state types
    // -------------------------------------------------------------------------

    private sealed class HolepunchAttempt
    {
        public IPEndPoint Target { get; init; }
        public IPeerConnection Relay { get; init; }
        public TaskCompletionSource<ITransportStream?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource TimeoutCts { get; init; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;
    }

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------

    private readonly ILogger<HolepunchManager> _logger;
    private readonly IPeerManager _peerManager;
    private readonly UtpSocketManager _utpSocketManager;
    private readonly Action<ITransportStream, IPEndPoint> _onHolepunchConnected;
    private readonly int _maxConcurrentAttempts;
    private readonly int _cooldownSeconds;

    private readonly ConcurrentDictionary<IPEndPoint, HolepunchAttempt> _pendingAttempts = new();
    private readonly ConcurrentDictionary<IPEndPoint, DateTime> _cooldowns = new();
    private readonly SemaphoreSlim _concurrencyGate;

    private bool _disposed;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public HolepunchManager(
        ILogger<HolepunchManager> logger,
        IPeerManager peerManager,
        UtpSocketManager utpSocketManager,
        Action<ITransportStream, IPEndPoint> onHolepunchConnected,
        int maxConcurrentAttempts = 8,
        int cooldownSeconds = 60)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _peerManager = peerManager ?? throw new ArgumentNullException(nameof(peerManager));
        _utpSocketManager = utpSocketManager ?? throw new ArgumentNullException(nameof(utpSocketManager));
        _onHolepunchConnected = onHolepunchConnected ?? throw new ArgumentNullException(nameof(onHolepunchConnected));
        _maxConcurrentAttempts = maxConcurrentAttempts;
        _cooldownSeconds = cooldownSeconds;
        _concurrencyGate = new SemaphoreSlim(maxConcurrentAttempts, maxConcurrentAttempts);
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Initiator role: send a Rendezvous through a relay peer and wait for the resulting
    /// uTP connection. Returns the stream on success, or null on timeout/error.
    /// </summary>
    public async Task<ITransportStream?> InitiateAsync(IPEndPoint target, CancellationToken ct = default)
    {
        // 1. Cooldown check
        if (_cooldowns.TryGetValue(target, out var cooldownUntil) &&
            DateTime.UtcNow < cooldownUntil)
        {
            _logger.LogDebug("Holepunch to {Target} suppressed — cooldown active until {Until}", target, cooldownUntil);
            return null;
        }

        // 2. Acquire semaphore (non-blocking)
        bool acquired = await _concurrencyGate.WaitAsync(0, ct).ConfigureAwait(false);
        if (!acquired)
        {
            _logger.LogDebug("Holepunch to {Target} suppressed — too many concurrent attempts", target);
            return null;
        }

        // 3. Find relay
        HolepunchExtension? relayExtension = null;
        IPeerConnection? relay = null;

        foreach (var peer in _peerManager.ConnectedPeers)
        {
            if (peer is not PeerConnection pc)
                continue;

            var em = pc.ExtensionManager;
            if (em == null)
                continue;

            var ext = em.GetExtension("ut_holepunch") as HolepunchExtension;
            if (ext == null || !ext.RemoteExtensionId.HasValue)
                continue;

            // Check PEX heuristic: does this relay know the target?
            var pex = em.GetExtension("ut_pex") as PexExtension;
            if (pex == null || !pex.KnowsPeer(target))
                continue;

            relay = peer;
            relayExtension = ext;
            break;
        }

        if (relay == null || relayExtension == null)
        {
            _logger.LogDebug("Holepunch to {Target}: no suitable relay found", target);
            _concurrencyGate.Release();
            return null;
        }

        // 4. Build attempt
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var attempt = new HolepunchAttempt
        {
            Target = target,
            Relay = relay,
            TimeoutCts = timeoutCts
        };

        if (!_pendingAttempts.TryAdd(target, attempt))
        {
            _logger.LogDebug("Holepunch to {Target}: attempt already pending", target);
            _concurrencyGate.Release();
            return null;
        }

        try
        {
            // 5. Send Rendezvous
            _logger.LogDebug("Holepunch to {Target}: sending Rendezvous via {Relay}", target, relay.PeerInfo.EndPoint);
            await relayExtension.SendRendezvousAsync(target).ConfigureAwait(false);

            // 6. Wait on TCS with combined timeout+cancellation
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                return await attempt.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogDebug("Holepunch to {Target} timed out", target);
                AddCooldown(target);
                return null;
            }
        }
        finally
        {
            _pendingAttempts.TryRemove(target, out _);
            _concurrencyGate.Release();
        }
    }

    /// <summary>
    /// Relay role: forwards Connect messages to both sides.
    /// Called when we receive a Rendezvous from the initiator.
    /// </summary>
    public async Task HandleRendezvousAsync(IPeerConnection initiator, HolepunchMessage message)
    {
        var targetEndpoint = message.Endpoint;

        // 1. Validate initiator supports ut_holepunch
        if (initiator is not PeerConnection initiatorPc)
        {
            _logger.LogDebug("Rendezvous from non-PeerConnection peer, ignoring");
            return;
        }

        var initiatorHp = initiatorPc.ExtensionManager?.GetExtension("ut_holepunch") as HolepunchExtension;
        if (initiatorHp == null || !initiatorHp.RemoteExtensionId.HasValue)
        {
            _logger.LogDebug("Rendezvous from {Peer}: initiator lacks ut_holepunch, ignoring", initiator.PeerInfo.EndPoint);
            return;
        }

        // 2. Find target among connected peers
        PeerConnection? targetPc = null;
        foreach (var peer in _peerManager.ConnectedPeers)
        {
            if (peer is PeerConnection pc &&
                pc.PeerInfo.EndPoint.Equals(targetEndpoint))
            {
                targetPc = pc;
                break;
            }
        }

        // 3. Validate target
        if (targetPc == null)
        {
            _logger.LogDebug("Rendezvous from {Initiator}: target {Target} not connected — sending NoSuchPeer",
                initiator.PeerInfo.EndPoint, targetEndpoint);
            await initiatorHp.SendErrorAsync(targetEndpoint, HolepunchError.NoSuchPeer).ConfigureAwait(false);
            return;
        }

        if (!targetPc.IsConnected)
        {
            _logger.LogDebug("Rendezvous from {Initiator}: target {Target} disconnected — sending NotConnected",
                initiator.PeerInfo.EndPoint, targetEndpoint);
            await initiatorHp.SendErrorAsync(targetEndpoint, HolepunchError.NotConnected).ConfigureAwait(false);
            return;
        }

        var targetHp = targetPc.ExtensionManager?.GetExtension("ut_holepunch") as HolepunchExtension;
        if (targetHp == null || !targetHp.RemoteExtensionId.HasValue)
        {
            _logger.LogDebug("Rendezvous from {Initiator}: target {Target} lacks ut_holepunch — sending NoSupport",
                initiator.PeerInfo.EndPoint, targetEndpoint);
            await initiatorHp.SendErrorAsync(targetEndpoint, HolepunchError.NoSupport).ConfigureAwait(false);
            return;
        }

        // Self-connect guard
        if (targetPc == initiatorPc)
        {
            _logger.LogDebug("Rendezvous from {Initiator}: target is self — sending NoSelf",
                initiator.PeerInfo.EndPoint);
            await initiatorHp.SendErrorAsync(targetEndpoint, HolepunchError.NoSelf).ConfigureAwait(false);
            return;
        }

        // 4. Send Connect to target (telling it to connect to initiator)
        var initiatorEndpoint = initiator.PeerInfo.EndPoint;
        _logger.LogDebug("Relaying holepunch: {Initiator} <-> {Target}", initiatorEndpoint, targetEndpoint);

        await targetHp.SendConnectAsync(initiatorEndpoint).ConfigureAwait(false);

        // 5. Send Connect to initiator (telling it that target's endpoint is ready)
        await initiatorHp.SendConnectAsync(targetEndpoint).ConfigureAwait(false);
    }

    /// <summary>
    /// Target + Initiator response role: handle an incoming Connect message.
    /// If we have a pending attempt for this endpoint → initiator completing the handshake.
    /// Otherwise → we are the target and must punch through to the initiator.
    /// </summary>
    public async Task HandleConnectAsync(IPEndPoint remoteEndpoint, CancellationToken ct = default)
    {
        // Check if we are the initiator awaiting this endpoint
        if (_pendingAttempts.TryGetValue(remoteEndpoint, out var attempt))
        {
            _logger.LogDebug("Connect received for pending holepunch to {Target} — initiating uTP", remoteEndpoint);
            try
            {
                var socket = await _utpSocketManager.ConnectAsync(remoteEndpoint, ct).ConfigureAwait(false);
                var stream = new UtpTransportStream(socket);
                attempt.Completion.TrySetResult(stream);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Holepunch uTP connect to {Target} failed", remoteEndpoint);
                attempt.Completion.TrySetResult(null);
                AddCooldown(remoteEndpoint);
            }
            return;
        }

        // We are the target — check if we are already connected to the initiator
        bool alreadyConnected = false;
        foreach (var peer in _peerManager.ConnectedPeers)
        {
            if (peer.PeerInfo.EndPoint.Equals(remoteEndpoint))
            {
                alreadyConnected = true;
                break;
            }
        }

        if (alreadyConnected)
        {
            _logger.LogDebug("Holepunch Connect from {Endpoint}: already connected, ignoring", remoteEndpoint);
            return;
        }

        // Target role: initiate uTP to initiator and invoke callback
        _logger.LogDebug("Holepunch Connect from {Endpoint}: acting as target, punching through", remoteEndpoint);
        try
        {
            var socket = await _utpSocketManager.ConnectAsync(remoteEndpoint, ct).ConfigureAwait(false);
            var stream = new UtpTransportStream(socket);
            _onHolepunchConnected(stream, remoteEndpoint);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Holepunch uTP connect to initiator {Endpoint} failed (target role)", remoteEndpoint);
        }
    }

    /// <summary>
    /// Handles an Error message — fails the pending attempt and records a cooldown.
    /// </summary>
    public void HandleError(IPEndPoint target, HolepunchError error)
    {
        _logger.LogDebug("Holepunch error for {Target}: {Error}", target, error);

        if (_pendingAttempts.TryGetValue(target, out var attempt))
        {
            attempt.Completion.TrySetResult(null);
        }

        AddCooldown(target);
    }

    /// <summary>
    /// Dispatches a received holepunch message to the correct handler.
    /// </summary>
    public Task HandleMessageAsync(IPeerConnection sender, HolepunchMessage message, CancellationToken ct = default)
    {
        return message.Type switch
        {
            HolepunchMessageType.Rendezvous => HandleRendezvousAsync(sender, message),
            HolepunchMessageType.Connect    => HandleConnectAsync(message.Endpoint, ct),
            HolepunchMessageType.Error      => Task.Run(() => HandleError(message.Endpoint, message.ErrorCode), ct),
            _ => Task.CompletedTask
        };
    }

    /// <summary>
    /// Removes cooldown entries that have expired.
    /// </summary>
    public void CleanupExpiredCooldowns()
    {
        var cutoff = DateTime.UtcNow;
        foreach (var kvp in _cooldowns)
        {
            if (kvp.Value <= cutoff)
                _cooldowns.TryRemove(kvp.Key, out _);
        }
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _pendingAttempts)
        {
            kvp.Value.TimeoutCts.Cancel();
            kvp.Value.Completion.TrySetResult(null);
        }

        _pendingAttempts.Clear();
        _cooldowns.Clear();
        _concurrencyGate.Dispose();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void AddCooldown(IPEndPoint target)
    {
        _cooldowns[target] = DateTime.UtcNow.AddSeconds(_cooldownSeconds);
    }
}
