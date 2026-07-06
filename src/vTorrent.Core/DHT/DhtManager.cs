using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Core;
using vTorrent.Core.Network;
using vTorrent.Storage;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Interfaces;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Core.Utilities;

namespace vTorrent.Core.DHT
{
    /// <summary>
    /// High-level DHT manager that integrates DHT functionality with the torrent engine.
    /// Manages peer lookups, announcements, and coordinates DHT operations for all torrents.
    /// </summary>
    public class DhtManager : IDhtPeerSource, IDisposable
    {
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<DhtSettings> _dhtMonitor;
        private readonly DhtStatePersistence _statePersistence;
        private readonly IDhtTransport _transport;
        private DhtNode _node;
        private DhtNode? _i2pNode;
        private IDhtTransport? _i2pTransport;

        private readonly ConcurrentDictionary<string, TorrentDhtState> _torrentStates;
        private readonly ConcurrentDictionary<string, DateTime> _lastLookup;
        private Timer _announceTimer;
        private Timer _saveStateTimer;
        private bool _disposed;
        private DhtScrapeCache? _scrapeCache;

        /// <summary>
        /// Whether DHT is enabled and running.
        /// </summary>
        public bool IsRunning => _node?.IsRunning ?? false;

        /// <summary>
        /// BEP 33: Scrape data provider for DHT-based seed/peer counts.
        /// </summary>
        public IDhtScrapeProvider? ScrapeProvider => _scrapeCache;

        /// <summary>
        /// Event raised when peers are discovered for a torrent.
        /// </summary>
        public event Action<byte[], List<PeerInfo>> PeersDiscovered;

        /// <summary>
        /// Event raised when DHT state changes.
        /// </summary>
        public event Action<DhtState> StateChanged;

        /// <summary>
        /// Current DHT state.
        /// </summary>
        public DhtState CurrentState { get; private set; } = DhtState.Stopped;

        public DhtManager(IOptionsMonitor<DhtSettings> dhtMonitor, IDhtTransport transport, ILogger logger = null, TorrentDatabase database = null)
        {
            _dhtMonitor = dhtMonitor ?? throw new ArgumentNullException(nameof(dhtMonitor));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _logger = logger;
            _torrentStates = new ConcurrentDictionary<string, TorrentDhtState>();
            _lastLookup = new ConcurrentDictionary<string, DateTime>();

            // Initialize state persistence if database is provided
            if (database != null)
            {
                _statePersistence = new DhtStatePersistence(database, logger);
            }
        }

        /// <summary>
        /// Starts the DHT manager.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (!_dhtMonitor.CurrentValue.Enabled)
            {
                _logger?.LogInformation("DHT is disabled in settings");
                return;
            }

            if (_node != null && _node.IsRunning)
                return;

            try
            {
                UpdateState(DhtState.Starting);

                // Try to load persisted DHT state for faster bootstrap
                NodeId? existingNodeId = null;
                List<NodeEntry> cachedNodes = null;

                if (_statePersistence != null)
                {
                    var state = await _statePersistence.LoadStateAsync();
                    if (state != null)
                    {
                        existingNodeId = _statePersistence.GetStoredNodeId(state);
                        cachedNodes = _statePersistence.GetNodesFromState(state);
                        _logger?.LogInformation("Loaded DHT state: NodeId={NodeId}, CachedNodes={Count}",
                            existingNodeId?.ToShortHex() ?? "new", cachedNodes?.Count ?? 0);
                    }
                }

                _node = new DhtNode(_dhtMonitor, _transport, _logger, existingNodeId);
                _node.PeersFound += OnPeersFound;
                _node.NodesFound += OnNodesFound;

                // Add cached nodes before starting (they'll be used during bootstrap)
                if (cachedNodes != null && cachedNodes.Count > 0)
                {
                    _node.AddCachedNodes(cachedNodes);
                }

                await _node.StartAsync(cancellationToken);

                // BEP 33: Initialize scrape cache for DHT-based seed/peer estimates
                _scrapeCache = new DhtScrapeCache(async (infoHash) =>
                {
                    var result = new DhtScrapeResult(infoHash);
                    return result;
                });
                _scrapeCache.Start();

                // Start announce timer
                _announceTimer = new Timer(OnAnnounceTimer, null,
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMilliseconds(_dhtMonitor.CurrentValue.AnnounceIntervalMs));

                // Start periodic state save timer (every 15 minutes)
                if (_statePersistence != null)
                {
                    _saveStateTimer = new Timer(OnSaveStateTimer, null,
                        TimeSpan.FromMinutes(15),
                        TimeSpan.FromMinutes(15));
                }

                UpdateState(DhtState.Running);

                _logger?.LogInformation("DHT manager started");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start DHT manager");
                UpdateState(DhtState.Error);
                throw;
            }
        }

        /// <summary>
        /// Timer callback to periodically save DHT state.
        /// Per Fowler AsyncGuidance: timer callbacks use _ = DoAsyncWork(), not async void.
        /// </summary>
        private void OnSaveStateTimer(object state)
        {
            _ = SaveStateAsync();
        }

        /// <summary>
        /// Saves the current DHT state to disk.
        /// </summary>
        public async Task SaveStateAsync()
        {
            if (_statePersistence == null || _node == null || !_node.IsRunning)
                return;

            try
            {
                await _statePersistence.SaveStateAsync(_node.NodeId, _node.RoutingTable)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to save DHT state");
            }
        }

        /// <summary>
        /// Stops the DHT manager.
        /// </summary>
        public void Stop()
        {
            UpdateState(DhtState.Stopping);

            // Fire-and-forget: best-effort save during shutdown, don't block Stop()
            SaveStateAsync().FireAndForget(_logger);

            _saveStateTimer?.Dispose();
            _saveStateTimer = null;

            _announceTimer?.Dispose();
            _announceTimer = null;

            if (_node != null)
            {
                _node.PeersFound -= OnPeersFound;
                _node.NodesFound -= OnNodesFound;
                _node.Stop();
                _node.Dispose();
                _node = null;
            }

            StopI2pNode();

            UpdateState(DhtState.Stopped);
            _logger?.LogInformation("DHT manager stopped");
        }

        /// <summary>
        /// Creates and starts the I2P DHT node. Called when I2P becomes available.
        /// </summary>
        public async Task StartI2pNodeAsync(IDhtTransport i2pTransport, CancellationToken ct = default)
        {
            if (_i2pNode != null) return; // Already running

            _i2pTransport = i2pTransport;

            _i2pNode = new DhtNode(_dhtMonitor, i2pTransport, _logger);
            _i2pNode.PeersFound += OnPeersFound;
            _i2pNode.NodesFound += OnNodesFound;

            await _i2pNode.StartAsync(ct).ConfigureAwait(false);

            _logger?.LogInformation("I2P DHT node started");
        }

        /// <summary>
        /// Stops and disposes the I2P DHT node. Called when I2P becomes unavailable.
        /// </summary>
        public void StopI2pNode()
        {
            if (_i2pNode == null) return;

            _i2pNode.PeersFound -= OnPeersFound;
            _i2pNode.NodesFound -= OnNodesFound;
            _i2pNode.Stop();
            _i2pNode.Dispose();
            _i2pNode = null;

            _i2pTransport?.Dispose();
            _i2pTransport = null;

            _logger?.LogInformation("I2P DHT node stopped");
        }

        /// <summary>
        /// Registers a torrent with the DHT for peer discovery and announcement.
        /// </summary>
        public void RegisterTorrent(byte[] infoHash, int listenPort)
        {
            if (infoHash == null || infoHash.Length != 20)
                throw new ArgumentException("info_hash must be 20 bytes", nameof(infoHash));

            string key = Convert.ToHexString(infoHash);

            _torrentStates[key] = new TorrentDhtState
            {
                InfoHash = infoHash,
                ListenPort = listenPort,
                LastAnnounce = DateTime.MinValue,
                LastLookup = DateTime.MinValue,
                IsActive = true,
                // Initialize adaptive timing fields (libtorrent-style boost)
                ConnectedPeers = 0,
                BoostLookupsRemaining = DhtConstants.InitialBoostLookups,
                LastBoostLookup = DateTime.MinValue,
                RegistrationTime = DateTime.UtcNow
            };

            _logger?.LogDebug("Registered torrent {InfoHash} for DHT", key);

            // Trigger immediate lookup
            if (IsRunning)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await LookupPeersAsync(infoHash, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Initial DHT lookup failed for {InfoHash}", key);
                    }
                });
            }
        }

        /// <summary>
        /// Unregisters a torrent from the DHT.
        /// </summary>
        public void UnregisterTorrent(byte[] infoHash)
        {
            if (infoHash == null || infoHash.Length != 20) return;

            string key = Convert.ToHexString(infoHash);
            if (_torrentStates.TryRemove(key, out var state))
            {
                state.IsActive = false;
                _logger?.LogDebug("Unregistered torrent {InfoHash} from DHT", key);
            }
        }

        /// <summary>
        /// Looks up peers for a torrent.
        /// </summary>
        public async Task<List<PeerInfo>> LookupPeersAsync(byte[] infoHash, CancellationToken cancellationToken = default)
        {
            if (_node == null || !_node.IsRunning)
            {
                _logger?.LogWarning("DHT node is not running, cannot lookup peers");
                return new List<PeerInfo>();
            }

            string key = Convert.ToHexString(infoHash);

            // Rate limit lookups
            if (_lastLookup.TryGetValue(key, out var lastTime))
            {
                if ((DateTime.UtcNow - lastTime).TotalSeconds < 30)
                {
                    _logger?.LogDebug("Skipping lookup for {InfoHash}, too recent", key);
                    return new List<PeerInfo>();
                }
            }

            _lastLookup[key] = DateTime.UtcNow;

            try
            {
                var endpoints = await _node.GetPeersAsync(infoHash, cancellationToken);

                var peers = new List<PeerInfo>();
                foreach (var ep in endpoints)
                {
                    if (ep is IPEndPoint ipEp)
                        peers.Add(new PeerInfo(ipEp.Address, ipEp.Port, null, "dht"));
                    // Non-IP endpoints from clearnet node are unexpected; skip them
                }

                // Also lookup on I2P DHT if available
                if (_i2pNode != null)
                {
                    try
                    {
                        var i2pEndpoints = await _i2pNode.GetPeersAsync(infoHash, cancellationToken).ConfigureAwait(false);
                        if (i2pEndpoints != null)
                        {
                            foreach (var ep in i2pEndpoints)
                            {
                                if (ep is I2pEndPoint i2pEp)
                                    peers.Add(PeerInfo.FromI2p(i2pEp.Destination, "dht-i2p"));
                                else if (ep is IPEndPoint ipEp)
                                    peers.Add(new PeerInfo(ipEp.Address, ipEp.Port, null, "dht-i2p"));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "I2P DHT lookup failed for {InfoHash}", key);
                    }
                }

                _logger?.LogDebug("DHT lookup for {InfoHash} found {Count} peers", key, peers.Count);

                // Update state
                if (_torrentStates.TryGetValue(key, out var state))
                {
                    state.LastLookup = DateTime.UtcNow;
                    state.PeersFound += peers.Count;
                }

                return peers;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "DHT lookup failed for {InfoHash}", key);
                return new List<PeerInfo>();
            }
        }

        /// <summary>
        /// Announces a torrent to the DHT.
        /// </summary>
        public async Task AnnounceAsync(byte[] infoHash, int port, CancellationToken cancellationToken = default)
        {
            if (_node == null || !_node.IsRunning)
            {
                _logger?.LogWarning("DHT node is not running, cannot announce");
                return;
            }

            string key = Convert.ToHexString(infoHash);

            try
            {
                await _node.AnnounceAsync(infoHash, port, cancellationToken);

                // Also announce to I2P DHT if available
                if (_i2pNode != null)
                {
                    try
                    {
                        await _i2pNode.AnnounceAsync(infoHash, port, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "I2P DHT announce failed for {InfoHash}", key);
                    }
                }

                if (_torrentStates.TryGetValue(key, out var state))
                {
                    state.LastAnnounce = DateTime.UtcNow;
                }

                _logger?.LogDebug("Announced {InfoHash} to DHT on port {Port}", key, port);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "DHT announce failed for {InfoHash}", key);
            }
        }

        /// <summary>
        /// BEP 51: Queries a specific DHT node for its stored infohash samples.
        /// </summary>
        public async Task<SampleInfohashesResult> SampleInfohashesAsync(
            IPEndPoint target, CancellationToken ct = default)
        {
            if (_node == null || !IsRunning)
                return new SampleInfohashesResult();

            // Use a random target ID — BEP 51 uses it for returning close nodes
            var targetId = NodeId.GenerateRandom();
            return await _node.SampleInfohashesAsync(target, targetId, ct);
        }

        /// <summary>
        /// Gets DHT statistics.
        /// </summary>
        public DhtManagerStats GetStats()
        {
            var nodeStats = _node?.GetStats() ?? default;

            return new DhtManagerStats
            {
                IsRunning = IsRunning,
                State = CurrentState,
                NodeId = nodeStats.NodeId.ToString(),
                NumBuckets = nodeStats.NumBuckets,
                LiveNodes = nodeStats.LiveNodes,
                ReplacementNodes = nodeStats.ReplacementNodes,
                ConfirmedNodes = nodeStats.ConfirmedNodes,
                PendingQueries = nodeStats.PendingQueries,
                StoredInfoHashes = nodeStats.StoredInfoHashes,
                StoredPeers = nodeStats.StoredPeers,
                RegisteredTorrents = _torrentStates.Count,
                TotalPeersFound = _torrentStates.Values.Sum(s => s.PeersFound)
            };
        }

        /// <summary>
        /// Gets the state for a specific torrent.
        /// </summary>
        public TorrentDhtState GetTorrentState(byte[] infoHash)
        {
            string key = Convert.ToHexString(infoHash);
            _torrentStates.TryGetValue(key, out var state);
            return state;
        }

        /// <summary>
        /// Updates the connected peer count for a torrent.
        /// Used by orchestrator to enable adaptive DHT timing (libtorrent-style).
        /// Torrents with fewer peers than LowPeerThreshold get priority lookups.
        /// </summary>
        public void UpdateConnectedPeers(byte[] infoHash, int connectedPeers)
        {
            string key = Convert.ToHexString(infoHash);
            if (_torrentStates.TryGetValue(key, out var state))
            {
                state.ConnectedPeers = connectedPeers;
            }
        }

        private void OnPeersFound(byte[] infoHash, List<EndPoint> endpoints)
        {
            var peers = new List<PeerInfo>(endpoints.Count);
            foreach (var ep in endpoints)
            {
                if (ep is IPEndPoint ipEp)
                    peers.Add(new PeerInfo(ipEp.Address, ipEp.Port, null, "dht"));
                else if (ep is I2pEndPoint i2pEp)
                    peers.Add(PeerInfo.FromI2p(i2pEp.Destination, "dht"));
            }

            if (peers.Count > 0)
            {
                PeersDiscovered?.Invoke(infoHash, peers);
            }
        }

        private void OnNodesFound(List<NodeEntry> nodes)
        {
            _logger?.LogDebug("DHT found {Count} nodes", nodes.Count);
        }

        private void OnAnnounceTimer(object state)
        {
            if (!IsRunning) return;

            var activeCount = _torrentStates.Values.Count(s => s.IsActive);
            if (activeCount == 0) return;

            // libtorrent-style adaptive interval: spread lookups across torrents
            var adaptiveIntervalMs = Math.Max(
                _dhtMonitor.CurrentValue.AnnounceIntervalMs / activeCount,
                DhtConstants.MinLookupIntervalMs);

            var now = DateTime.UtcNow;

            foreach (var kvp in _torrentStates)
            {
                var torrentState = kvp.Value;
                if (!torrentState.IsActive) continue;

                var timeSinceLastLookup = (now - torrentState.LastLookup).TotalMilliseconds;

                // Check for boost lookups (new torrents get rapid initial lookups, like libtorrent's do_connect_boost)
                bool shouldBoostLookup = torrentState.BoostLookupsRemaining > 0 &&
                    (now - torrentState.LastBoostLookup).TotalMilliseconds >= DhtConstants.BoostLookupIntervalMs;

                // Priority torrents (few peers) get more frequent lookups
                bool isPriority = torrentState.ConnectedPeers < DhtConstants.LowPeerThreshold;
                var effectiveInterval = isPriority ? adaptiveIntervalMs : _dhtMonitor.CurrentValue.AnnounceIntervalMs / 2;

                // Perform lookup if boost needed OR regular interval elapsed
                if (shouldBoostLookup || timeSinceLastLookup >= effectiveInterval)
                {
                    var localState = torrentState; // Capture for closure
                    var localKey = kvp.Key;
                    var wasBoost = shouldBoostLookup;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await LookupPeersAsync(localState.InfoHash, CancellationToken.None);

                            if (wasBoost)
                            {
                                localState.BoostLookupsRemaining--;
                                localState.LastBoostLookup = DateTime.UtcNow;
                                _logger?.LogDebug("Boost lookup for {InfoHash}, {Remaining} remaining",
                                    localKey, localState.BoostLookupsRemaining);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Periodic lookup failed for {InfoHash}", localKey);
                        }
                    });
                }

                // Check if announce is needed (separate from lookups)
                if ((now - torrentState.LastAnnounce).TotalMilliseconds >= _dhtMonitor.CurrentValue.AnnounceIntervalMs)
                {
                    var localState = torrentState;
                    var localKey = kvp.Key;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await AnnounceAsync(localState.InfoHash, localState.ListenPort, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Periodic announce failed for {InfoHash}", localKey);
                        }
                    });
                }
            }
        }

        private void UpdateState(DhtState newState)
        {
            if (CurrentState != newState)
            {
                CurrentState = newState;
                StateChanged?.Invoke(newState);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
        }
    }

    /// <summary>
    /// DHT operational state.
    /// </summary>
    public enum DhtState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Error
    }

    /// <summary>
    /// State tracking for a single torrent in DHT.
    /// Extended with adaptive timing fields (libtorrent-style).
    /// </summary>
    public class TorrentDhtState
    {
        public byte[] InfoHash { get; set; }
        public int ListenPort { get; set; }
        public DateTime LastAnnounce { get; set; }
        public DateTime LastLookup { get; set; }
        public int PeersFound { get; set; }
        public bool IsActive { get; set; }

        // Adaptive timing fields (libtorrent-style)
        /// <summary>
        /// Number of currently connected peers for this torrent.
        /// Updated by orchestrator to enable priority lookups for low-peer torrents.
        /// </summary>
        public int ConnectedPeers { get; set; }

        /// <summary>
        /// Remaining boost lookups for newly registered torrents.
        /// libtorrent does rapid initial lookups ("connect boost").
        /// </summary>
        public int BoostLookupsRemaining { get; set; }

        /// <summary>
        /// Time of last boost lookup.
        /// </summary>
        public DateTime LastBoostLookup { get; set; }

        /// <summary>
        /// When this torrent was registered with DHT.
        /// </summary>
        public DateTime RegistrationTime { get; set; }
    }

    /// <summary>
    /// DHT manager statistics.
    /// </summary>
    public struct DhtManagerStats
    {
        public bool IsRunning { get; set; }
        public DhtState State { get; set; }
        public string NodeId { get; set; }
        public int NumBuckets { get; set; }
        public int LiveNodes { get; set; }
        public int ReplacementNodes { get; set; }
        public int ConfirmedNodes { get; set; }
        public int PendingQueries { get; set; }
        public int StoredInfoHashes { get; set; }
        public int StoredPeers { get; set; }
        public int RegisteredTorrents { get; set; }
        public int TotalPeersFound { get; set; }
    }
}
