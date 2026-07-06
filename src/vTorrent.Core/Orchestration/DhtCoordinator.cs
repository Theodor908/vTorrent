using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Core.DHT;
using vTorrent.Core.LocalPeerDiscovery;
using vTorrent.Core.Network;
using vTorrent.Core.Network.I2P;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Interfaces.Transport;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Coordinates DHT (Distributed Hash Table) and LPD (Local Peer Discovery) services.
/// Extracted from TorrentOrchestrator as part of god class decomposition (Phase 5, Task 5.2).
/// Manages peer discovery, torrent registration, and pending peer caching.
/// </summary>
internal class DhtCoordinator
{
    private readonly TorrentOrchestrator _orch;
    private readonly ILogger<DhtCoordinator> _logger;
    private readonly UdpSocketManager? _udpSocketManager;
    private readonly IOptionsMonitor<DhtSettings>? _dhtMonitor;
    private readonly I2pService? _i2pService;

    // DHT (Distributed Hash Table)
    private DhtManager? _dhtManager;

    // LPD (Local Peer Discovery - BEP 14)
    private LpdService? _lpdService;

    // DHT peer queue for torrents without active engines (libtorrent-style caching)
    private readonly ConcurrentDictionary<string, ConcurrentQueue<PeerInfo>> _pendingDhtPeers = new();
    private const int MaxPendingPeersPerTorrent = 100;

    #region Properties

    /// <summary>
    /// DHT manager for distributed peer discovery (null if DHT disabled)
    /// </summary>
    public DhtManager? DhtManager => _dhtManager;

    /// <summary>
    /// Whether DHT is enabled and running
    /// </summary>
    public bool IsDhtRunning => _dhtManager?.IsRunning ?? false;

    /// <summary>
    /// Whether DHT is currently initializing (bootstrapping)
    /// </summary>
    public bool IsDhtInitializing { get; private set; }

    /// <summary>
    /// Whether DHT is enabled in settings
    /// </summary>
    public bool IsDhtEnabled => (_dhtMonitor?.CurrentValue ?? _orch.Persistence.Settings.Dht).Enabled;

    /// <summary>
    /// Number of live DHT nodes in the routing table
    /// </summary>
    public int DhtNodeCount => _dhtManager?.GetStats().LiveNodes ?? 0;

    /// <summary>
    /// Pending DHT peers for torrents without active engines
    /// </summary>
    internal ConcurrentDictionary<string, ConcurrentQueue<PeerInfo>> PendingDhtPeers => _pendingDhtPeers;

    #endregion

    public DhtCoordinator(TorrentOrchestrator orchestrator, ILoggerFactory loggerFactory,
        UdpSocketManager? udpSocketManager = null,
        IOptionsMonitor<DhtSettings>? dhtMonitor = null,
        I2pService? i2pService = null)
    {
        _orch = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _logger = loggerFactory.CreateLogger<DhtCoordinator>();
        _udpSocketManager = udpSocketManager;
        _dhtMonitor = dhtMonitor;
        _i2pService = i2pService;

        if (_i2pService != null)
            _i2pService.AvailabilityChanged += OnI2pAvailabilityChanged;
    }

    /// <summary>
    /// Starts DHT initialization in the background (non-blocking).
    /// </summary>
    internal void StartDhtInBackground()
    {
        var dhtSettings = _dhtMonitor?.CurrentValue ?? _orch.Persistence.Settings.Dht;
        if (!dhtSettings.Enabled)
        {
            _logger.LogInformation("DHT is disabled in settings");
            RaiseDhtStateChanged(false, false);
            return;
        }

        // Start in background to not block UI
        _ = Task.Run(async () =>
        {
            try
            {
                await InitializeDhtAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background DHT initialization failed");
            }
        });
    }

    /// <summary>
    /// Initialize DHT (internal async method).
    /// </summary>
    private async Task InitializeDhtAsync(CancellationToken cancellationToken)
    {
        var dhtSettings = _dhtMonitor?.CurrentValue ?? _orch.Persistence.Settings.Dht;

        try
        {
            IsDhtInitializing = true;
            RaiseDhtStateChanged(false, true);

            _logger.LogInformation("Initializing DHT on port {Port}...", dhtSettings.Port);

            IDhtTransport transport;
            if (_udpSocketManager != null)
            {
                transport = new UdpDhtTransport(_udpSocketManager);
            }
            else
            {
                var port = _dhtMonitor!.CurrentValue.Port;
                transport = new StandaloneDhtTransport(port);
            }

            _dhtManager = new DhtManager(_dhtMonitor!, transport, _orch.LoggerFactoryInternal.CreateLogger<DhtManager>(), _orch.Persistence.Database);

            // Wire peer discovery event
            _dhtManager.PeersDiscovered += OnDhtPeersDiscovered;
            _dhtManager.StateChanged += OnDhtManagerStateChanged;

            // Start DHT (this includes bootstrapping)
            await _dhtManager.StartAsync(cancellationToken).ConfigureAwait(false);

            // Start I2P DHT node if I2P is already connected
            try
            {
                await TryStartI2pDhtAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start I2P DHT node during init");
            }

            IsDhtInitializing = false;
            RaiseDhtStateChanged(true, false);

            _logger.LogInformation("DHT started successfully");

            // Initialize Local Peer Discovery (BEP 14)
            try
            {
                _lpdService = new LpdService(_orch.LoggerFactoryInternal.CreateLogger<LpdService>(), _orch.ConnectionMonitorInternal);
                _lpdService.PeersDiscovered += OnLpdPeersDiscovered;
                await _lpdService.StartAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("LPD started successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start LPD (non-fatal)");
                _lpdService?.Dispose();
                _lpdService = null;
            }

            // Register all active torrents with DHT and LPD
            foreach (var managed in _orch.TorrentsInternal.Where(t => t.Engine != null))
            {
                RegisterTorrentWithDht(managed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize DHT");
            IsDhtInitializing = false;

            if (_dhtManager != null)
            {
                _dhtManager.PeersDiscovered -= OnDhtPeersDiscovered;
                _dhtManager.StateChanged -= OnDhtManagerStateChanged;
                _dhtManager.Dispose();
                _dhtManager = null;
            }

            RaiseDhtStateChanged(false, false);
        }
    }

    /// <summary>
    /// Enable DHT at runtime.
    /// </summary>
    public async Task EnableDhtAsync()
    {
        if (_dhtManager?.IsRunning == true)
        {
            _logger.LogDebug("DHT is already running");
            return;
        }

        // Update settings
        await _orch.Persistence.UpdateSettingsAsync(s => s.Dht.Enabled = true).ConfigureAwait(false);

        // Start DHT in background
        StartDhtInBackground();
    }

    /// <summary>
    /// Disable DHT at runtime.
    /// </summary>
    public async Task DisableDhtAsync()
    {
        // Update settings
        await _orch.Persistence.UpdateSettingsAsync(s => s.Dht.Enabled = false).ConfigureAwait(false);

        // Stop DHT if running
        await StopDhtAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Stop DHT gracefully.
    /// </summary>
    internal async Task StopDhtAsync()
    {
        if (_dhtManager == null)
            return;

        _logger.LogInformation("Stopping DHT...");

        // Unregister all torrents
        foreach (var managed in _orch.TorrentsInternal)
        {
            UnregisterTorrentFromDht(managed);
        }

        // Stop LPD
        if (_lpdService != null)
        {
            _lpdService.PeersDiscovered -= OnLpdPeersDiscovered;
            _lpdService.Stop();
            _lpdService.Dispose();
            _lpdService = null;
            _logger.LogDebug("LPD stopped");
        }

        _dhtManager.PeersDiscovered -= OnDhtPeersDiscovered;
        _dhtManager.StateChanged -= OnDhtManagerStateChanged;
        _dhtManager.Stop();
        _dhtManager.Dispose();
        _dhtManager = null;

        IsDhtInitializing = false;
        RaiseDhtStateChanged(false, false);

        _logger.LogInformation("DHT stopped");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Toggle DHT on/off.
    /// </summary>
    public async Task ToggleDhtAsync()
    {
        if (IsDhtRunning || IsDhtInitializing)
        {
            await DisableDhtAsync().ConfigureAwait(false);
        }
        else
        {
            await EnableDhtAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Update connected peer count for a torrent in DHT.
    /// Called from stats update timer.
    /// </summary>
    internal void UpdateDhtPeerCount(string infoHash, int connectedPeers)
    {
        if (_dhtManager == null || string.IsNullOrEmpty(infoHash))
            return;

        try
        {
            var infoHashBytes = Convert.FromHexString(infoHash);
            _dhtManager.UpdateConnectedPeers(infoHashBytes, connectedPeers);
        }
        catch { /* Ignore conversion errors */ }
    }

    /// <summary>
    /// Broadcast current DHT state (for periodic UI refresh).
    /// </summary>
    internal void BroadcastState()
    {
        if (IsDhtRunning || IsDhtInitializing)
        {
            RaiseDhtStateChanged(IsDhtRunning, IsDhtInitializing);
        }
    }

    private void OnDhtManagerStateChanged(DHT.DhtState state)
    {
        _logger.LogDebug("DHT state changed to: {State}", state);
        RaiseDhtStateChanged(state == DHT.DhtState.Running, state == DHT.DhtState.Starting);
    }

    private void OnI2pAvailabilityChanged(object? sender, I2pAvailability availability)
    {
        if (_dhtManager == null) return;

        if (availability == I2pAvailability.Available)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await TryStartI2pDhtAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to start I2P DHT node on availability change");
                }
            });
        }
        else if (availability == I2pAvailability.Unavailable)
        {
            _dhtManager.StopI2pNode();
        }
    }

    private async Task TryStartI2pDhtAsync(CancellationToken ct)
    {
        if (_i2pService == null || _dhtManager == null) return;
        if (!_i2pService.IsConnected) return;

        var session = _i2pService.Session;
        if (session?.LocalDestination == null) return;
        if (session.SessionId == null) return;

        // SAM datagram port is typically SamPort - 1 (e.g., 7655 for SAM on 7656)
        var datagramPort = session.SamPort - 1;
        var datagramClient = new I2pDatagramClient(session.SamHostname, datagramPort, session.SessionId);
        await datagramClient.StartAsync(ct).ConfigureAwait(false);

        var port = (ushort)(_dhtMonitor?.CurrentValue.Port ?? 6881);
        var transport = new I2pDhtTransport(
            datagramClient,
            session.LocalDestination,
            port,
            _orch.LoggerFactoryInternal.CreateLogger<I2pDhtTransport>());

        await _dhtManager.StartI2pNodeAsync(transport, ct).ConfigureAwait(false);
    }

    private void RaiseDhtStateChanged(bool isRunning, bool isInitializing)
    {
        var nodeCount = _dhtManager?.GetStats().LiveNodes ?? 0;
        _orch.RaiseDhtStateChanged(isRunning, isInitializing, nodeCount);
    }

    /// <summary>
    /// Handle peers discovered via DHT.
    /// Queues peers when engine not running (libtorrent-style caching).
    /// </summary>
    private void OnDhtPeersDiscovered(byte[] infoHash, List<PeerInfo> peers)
    {
        var infoHashHex = Convert.ToHexString(infoHash);
        _logger.LogDebug("DHT discovered {Count} peers for {InfoHash}", peers.Count, infoHashHex);

        // Find the managed torrent
        var managed = _orch.TorrentsInternal.Find(infoHashHex);
        if (managed == null)
        {
            _logger.LogDebug("No torrent registered for {InfoHash}, ignoring DHT peers", infoHashHex);
            return;
        }

        // BEP 27: reject DHT peers for private torrents
        if (managed.IsPrivate)
        {
            _logger.LogDebug("Rejecting {Count} DHT peers for private torrent {InfoHash}",
                peers.Count, infoHashHex);
            return;
        }

        if (managed.Engine == null)
        {
            // Queue peers for when engine starts (like libtorrent's dht_storage::torrent_entry)
            var queue = _pendingDhtPeers.GetOrAdd(infoHashHex, _ => new ConcurrentQueue<PeerInfo>());

            int added = 0;
            foreach (var peer in peers)
            {
                // Limit queue size to prevent memory bloat
                if (queue.Count >= MaxPendingPeersPerTorrent)
                {
                    // Remove oldest peer to make room
                    queue.TryDequeue(out _);
                }
                queue.Enqueue(peer);
                added++;
            }

            _logger.LogDebug("Queued {Count} DHT peers for {InfoHash} (queue size: {QueueSize})",
                added, infoHashHex, queue.Count);
            return;
        }

        // Engine is running, add peers directly
        foreach (var peer in peers)
        {
            try
            {
                _ = managed.Engine.AddPeerFromDhtAsync(peer);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to add DHT peer {Peer}", peer);
            }
        }
    }

    /// <summary>
    /// Handle peers discovered via LPD (Local Peer Discovery).
    /// Same injection path as DHT peers.
    /// </summary>
    private void OnLpdPeersDiscovered(byte[] infoHash, List<PeerInfo> peers)
    {
        var infoHashHex = Convert.ToHexString(infoHash);
        _logger.LogDebug("LPD discovered {Count} peers for {InfoHash}", peers.Count, infoHashHex);

        var managed = _orch.TorrentsInternal.Find(infoHashHex);
        if (managed?.Engine == null)
            return;

        // BEP 27: reject LPD peers for private torrents
        if (managed.IsPrivate)
        {
            _logger.LogDebug("Rejecting {Count} LPD peers for private torrent {InfoHash}",
                peers.Count, infoHashHex);
            return;
        }

        foreach (var peer in peers)
        {
            try
            {
                _ = managed.Engine.AddPeerFromDhtAsync(peer);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to add LPD peer {Peer}", peer);
            }
        }
    }

    /// <summary>
    /// Flush queued DHT peers to a torrent that just started.
    /// Called after engine initialization to inject peers discovered during metadata download.
    /// </summary>
    internal async Task FlushPendingDhtPeersAsync(ManagedTorrent managed)
    {
        if (managed.Engine == null)
            return;

        // BEP 27: don't flush DHT peers into private torrents
        if (managed.IsPrivate)
        {
            _pendingDhtPeers.TryRemove(managed.InfoHash, out _);
            return;
        }

        if (!_pendingDhtPeers.TryRemove(managed.InfoHash, out var queue))
            return;

        int flushed = 0;
        while (queue.TryDequeue(out var peer))
        {
            // Check engine is still valid (could have stopped during flush)
            if (managed.Engine == null)
                break;

            try
            {
                await managed.Engine.AddPeerFromDhtAsync(peer).ConfigureAwait(false);
                flushed++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to add queued DHT peer {Peer}", peer);
            }
        }

        if (flushed > 0)
        {
            _logger.LogDebug("Flushed {Count} queued DHT peers to {Name}", flushed, managed.Name);
        }
    }

    /// <summary>
    /// Register a torrent with DHT for peer discovery and announcement.
    /// </summary>
    internal void RegisterTorrentWithDht(ManagedTorrent managed)
    {
        if (_dhtManager == null || !_dhtManager.IsRunning)
            return;

        // BEP 27: private torrents must not be announced via DHT or LPD
        if (managed.IsPrivate)
        {
            _logger.LogDebug("Skipping DHT/LPD registration for private torrent {Name}", managed.Name);
            return;
        }

        // I2P mixed mode: skip clearnet DHT for pure I2P torrents
        if (managed.IsI2p)
        {
            var allowMixed = _orch.I2pMonitorInternal?.CurrentValue.AllowMixedMode == true;
            if (!allowMixed)
            {
                _logger.LogDebug("Skipping clearnet DHT/LPD registration for pure I2P torrent {Name}", managed.Name);
                return;
            }
        }

        try
        {
            var infoHashBytes = Convert.FromHexString(managed.InfoHash);
            // RUNTIME: use monitor for live connection settings, fallback to persistence
            var listenPort = (_orch.ConnectionMonitorInternal?.CurrentValue ?? _orch.Persistence.Settings.Connection).ListenPort;

            _dhtManager.RegisterTorrent(infoHashBytes, listenPort);
            _logger.LogDebug("Registered torrent {Name} with DHT", managed.Name);

            // Also register with LPD
            if (_lpdService?.IsRunning == true)
            {
                _lpdService.RegisterTorrent(infoHashBytes, listenPort);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register torrent {Name} with DHT", managed.Name);
        }
    }

    /// <summary>
    /// Unregister a torrent from DHT.
    /// </summary>
    internal void UnregisterTorrentFromDht(ManagedTorrent managed)
    {
        if (_dhtManager == null || !_dhtManager.IsRunning)
            return;

        try
        {
            var infoHashBytes = Convert.FromHexString(managed.InfoHash);
            _dhtManager.UnregisterTorrent(infoHashBytes);
            _logger.LogDebug("Unregistered torrent {Name} from DHT", managed.Name);

            // Also unregister from LPD
            if (_lpdService?.IsRunning == true)
            {
                _lpdService.UnregisterTorrent(infoHashBytes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unregister torrent {Name} from DHT", managed.Name);
        }
    }
}
