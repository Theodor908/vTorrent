using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Core.Network;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.DHT
{
    /// <summary>
    /// Main DHT node implementation.
    /// Handles UDP communication, message routing, and coordinates all DHT operations.
    /// Delegates network transport to an <see cref="IDhtTransport"/> implementation.
    /// </summary>
    public class DhtNode : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<DhtSettings> _dhtMonitor;
        private readonly RoutingTable _routingTable;
        private readonly RpcManager _rpcManager;
        private readonly IDhtStorage _storage;
        private readonly TokenManager _tokenManager;
        private readonly DosBlocker _dosBlocker;
        private readonly IDhtTransport _transport;

        private CancellationTokenSource _cts;
        private Timer _maintenanceTimer;

        private bool _isRunning;
        private bool _disposed;
        private DateTime _lastBootstrap = DateTime.MinValue;

        /// <summary>
        /// Our node ID.
        /// </summary>
        public NodeId NodeId => _routingTable.Id;

        /// <summary>
        /// Whether the node is currently running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Event raised when new peers are discovered for an info_hash.
        /// Endpoints are <see cref="IPEndPoint"/> for clearnet peers or <see cref="I2pEndPoint"/> for I2P peers.
        /// </summary>
        public event Action<byte[], List<EndPoint>> PeersFound;

        /// <summary>
        /// Event raised when nodes are discovered.
        /// </summary>
        public event Action<List<NodeEntry>> NodesFound;

        /// <summary>
        /// Gets the routing table for state persistence.
        /// </summary>
        internal RoutingTable RoutingTable => _routingTable;

        public DhtNode(IOptionsMonitor<DhtSettings> dhtMonitor, IDhtTransport transport, ILogger logger = null, NodeId? existingNodeId = null)
        {
            _dhtMonitor = dhtMonitor ?? throw new ArgumentNullException(nameof(dhtMonitor));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _logger = logger;

            // Use existing node ID if provided (from persisted state), otherwise generate new
            var nodeId = existingNodeId ?? NodeId.GenerateRandom();

            _routingTable = new RoutingTable(nodeId, dhtMonitor);
            _rpcManager = new RpcManager(dhtMonitor, logger);
            _storage = new DhtDefaultStorage(dhtMonitor);
            _tokenManager = new TokenManager(TimeSpan.FromMilliseconds(DhtConstants.TokenRefreshIntervalMs));
            _dosBlocker = new DosBlocker(dhtMonitor, logger);

            _rpcManager.QueryTimedOut += OnQueryTimedOut;
        }

        /// <summary>
        /// Adds cached nodes from persisted state to the routing table for bootstrap.
        /// </summary>
        public void AddCachedNodes(List<NodeEntry> nodes)
        {
            if (nodes == null || nodes.Count == 0)
                return;

            _logger?.LogInformation("Adding {Count} cached nodes from persisted DHT state", nodes.Count);

            int added = 0;
            foreach (var node in nodes)
            {
                try
                {
                    // Add as router node (will be converted to regular node on successful ping)
                    _routingTable.AddRouterNode(node.NetworkEndPoint);
                    added++;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to add cached node {Node}", node.NetworkEndPoint);
                }
            }

            _logger?.LogInformation("Added {Count} cached nodes as router nodes", added);
        }

        /// <summary>
        /// Starts the DHT node.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_isRunning) return;

            _logger?.LogInformation("Starting DHT node {NodeId} on port {Port}",
                NodeId.ToShortHex(), _dhtMonitor.CurrentValue.Port);

            try
            {
                _cts = new CancellationTokenSource();

                _transport.SetPacketHandler(ProcessIncomingPacket);
                await _transport.StartAsync(cancellationToken).ConfigureAwait(false);

                // Add bootstrap nodes as router nodes
                _logger?.LogInformation("Resolving {Count} bootstrap nodes...", _dhtMonitor.CurrentValue.BootstrapNodes.Length);
                foreach (var bootstrapNode in _dhtMonitor.CurrentValue.BootstrapNodes)
                {
                    try
                    {
                        var parts = bootstrapNode.Split(':');
                        if (parts.Length == 2 && int.TryParse(parts[1], out int port))
                        {
                            _logger?.LogDebug("Resolving bootstrap node: {Node}", bootstrapNode);
                            var addresses = await Dns.GetHostAddressesAsync(parts[0], cancellationToken);
                            var ipv4Addresses = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
                            _logger?.LogInformation("Resolved {Node} to {Count} IPv4 addresses: {Addresses}",
                                bootstrapNode, ipv4Addresses.Count, string.Join(", ", ipv4Addresses));
                            foreach (var addr in ipv4Addresses)
                            {
                                _routingTable.AddRouterNode(new IPEndPoint(addr, port));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to resolve bootstrap node: {Node}", bootstrapNode);
                    }
                }
                _logger?.LogInformation("Total router nodes after DNS resolution: {Count}", _routingTable.RouterNodes.Count);

                _isRunning = true;

                // Start maintenance timer
                _maintenanceTimer = new Timer(OnMaintenanceTick, null,
                    DhtConstants.TickIntervalMs, DhtConstants.TickIntervalMs);

                // Bootstrap
                await BootstrapAsync(cancellationToken);

                _logger?.LogInformation("DHT node started successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start DHT node");
                Stop();
                throw;
            }
        }

        /// <summary>
        /// Stops the DHT node.
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _logger?.LogInformation("Stopping DHT node");

            _isRunning = false;
            _maintenanceTimer?.Dispose();
            _cts?.Cancel();

            _rpcManager.CancelAll();

            _logger?.LogInformation("DHT node stopped");
        }

        /// <summary>
        /// Bootstraps the DHT by finding nodes close to ourselves.
        /// </summary>
        public async Task BootstrapAsync(CancellationToken cancellationToken = default)
        {
            _logger?.LogInformation("Bootstrapping DHT...");
            _lastBootstrap = DateTime.UtcNow;

            // Ping all router nodes first
            var routerNodes = _routingTable.RouterNodes;
            _logger?.LogInformation("Pinging {Count} router nodes: {Nodes}",
                routerNodes.Count, string.Join(", ", routerNodes));

            var pingResults = new List<(EndPoint node, bool success)>();
            var pingTasks = routerNodes.Select(async node =>
            {
                bool success = false;
                if (node is IPEndPoint ipNode)
                    success = await PingAsync(ipNode, cancellationToken);
                lock (pingResults)
                {
                    pingResults.Add((node, success));
                }
                return success;
            }).ToList();

            try
            {
                await Task.WhenAll(pingTasks);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Some ping tasks failed during bootstrap");
            }

            var successCount = pingResults.Count(r => r.success);
            _logger?.LogInformation("Ping results: {Success}/{Total} responded. Details: {Details}",
                successCount, pingResults.Count,
                string.Join(", ", pingResults.Select(r => $"{r.node}={r.success}")));

            // Find nodes close to ourselves
            _logger?.LogInformation("Running find_node for self (NodeId: {NodeId})...", NodeId.ToShortHex());
            await FindNodeAsync(NodeId, cancellationToken);

            var stats = _routingTable.GetStats();
            _logger?.LogInformation("Bootstrap complete. Routing table: {LiveNodes} live nodes in {NumBuckets} buckets",
                stats.LiveNodes, stats.NumBuckets);
        }

        /// <summary>
        /// Sends a ping to a node.
        /// </summary>
        public async Task<bool> PingAsync(IPEndPoint target, CancellationToken cancellationToken = default)
        {
            var query = DhtMessage.CreatePingQuery(_rpcManager.GenerateTransactionId(), NodeId, readOnly: _dhtMonitor.CurrentValue.ReadOnly);

            try
            {
                var response = await SendQueryAsync(query, target, cancellationToken);
                if (response != null)
                {
                    _routingTable.NodeSeen(response.NodeId, target, 0);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Ping to {Target} failed", target);
            }

            return false;
        }

        /// <summary>
        /// Performs a find_node lookup for the target ID.
        /// Returns the closest nodes found.
        /// </summary>
        public async Task<List<NodeEntry>> FindNodeAsync(NodeId target, CancellationToken cancellationToken = default)
        {
            _logger?.LogInformation("[FIND_NODE] === Starting find_node for {Target} ===", target.ToShortHex());

            var closest = new SortedDictionary<NodeId, NodeEntry>(
                Comparer<NodeId>.Create((a, b) =>
                    NodeId.Distance(a, target).CompareTo(NodeId.Distance(b, target))));

            var queried = new HashSet<string>();
            var pending = new List<Task<DhtMessage>>();

            // Get routing table stats to decide if we need replacement nodes
            var rtStats = _routingTable.GetStats();
            bool needsReplacements = rtStats.LiveNodes < DhtConstants.BucketSize;

            // Start with closest known nodes - include replacements if routing table is sparse
            var initialNodes = _routingTable.FindClosestNodes(target, DhtConstants.BucketSize * 2,
                includeQuestionable: true, includeReplacements: needsReplacements);
            _logger?.LogInformation("[FIND_NODE] Initial nodes from routing table: {Count} (live={Live}, replacement={Repl}, includeReplacements={IncludeRepl})",
                initialNodes.Count, rtStats.LiveNodes, rtStats.ReplacementNodes, needsReplacements);
            foreach (var node in initialNodes)
            {
                closest[node.Id] = node;
            }

            // If we have fewer than 3 nodes, also include router nodes (libtorrent threshold)
            int routerCount = 0;
            if (initialNodes.Count < 3)
            {
                _logger?.LogWarning("[FIND_NODE] Very few initial nodes ({Count}), adding router nodes", initialNodes.Count);
                foreach (var router in _routingTable.RouterNodes)
                {
                    // Use unique placeholder IDs for each router
                    var routerId = NodeId.GenerateRandom();
                    NodeEntry routerNode = router is IPEndPoint ipRouter
                        ? new NodeEntry(routerId, ipRouter)
                        : new NodeEntry(routerId, router, 0);
                    if (!closest.Values.Any(n => n.NetworkEndPoint.Equals(router)))
                    {
                        closest[routerId] = routerNode;
                        routerCount++;
                    }
                }
            }
            _logger?.LogInformation("[FIND_NODE] Added {Count} router nodes, total closest: {Total}", routerCount, closest.Count);

            int iterations = 0;
            int maxIterations = 20;
            int totalNodesDiscovered = 0;

            while (iterations++ < maxIterations && !cancellationToken.IsCancellationRequested)
            {
                // Find unqueried nodes to query
                var toQuery = closest.Values
                    .Where(n => !queried.Contains($"{n.Address}:{n.Port}"))
                    .Take(_dhtMonitor.CurrentValue.SearchBranching)
                    .ToList();

                _logger?.LogDebug("[FIND_NODE] Iteration {Iteration}: {ToQuery} nodes to query", iterations, toQuery.Count);

                if (toQuery.Count == 0)
                {
                    _logger?.LogInformation("[FIND_NODE] No more nodes to query, ending");
                    break;
                }

                // Send queries
                var tasks = new List<Task<(NodeEntry node, DhtMessage response)>>();
                foreach (var node in toQuery)
                {
                    string key = $"{node.Address}:{node.Port}";
                    queried.Add(key);

                    tasks.Add(QueryFindNodeAsync(node.EndPoint, target, cancellationToken));
                }

                // Wait for all with timeout
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Some find_node queries failed");
                }

                // Process responses
                bool foundCloser = false;
                int iterationNodes = 0;
                int iterationResponses = 0;

                foreach (var task in tasks)
                {
                    try
                    {
                        var (node, response) = await task;
                        if (response?.Nodes != null)
                        {
                            iterationResponses++;

                            // IMPORTANT: Mark the responding node as "seen" with Pinged=true
                            // This promotes it from replacement to live bucket
                            if (!response.NodeId.IsZero() && node != null)
                            {
                                _routingTable.NodeSeen(response.NodeId, node.EndPoint, 0);
                            }

                            var nodes = ParseCompactNodesViaTransport(response.Nodes);
                            _logger?.LogDebug("[FIND_NODE] Response from {Endpoint}: {Count} nodes",
                                node?.EndPoint, nodes.Count);
                            foreach (var newNode in nodes)
                            {
                                if (!closest.ContainsKey(newNode.Id))
                                {
                                    closest[newNode.Id] = newNode;
                                    foundCloser = true;
                                    iterationNodes++;
                                    totalNodesDiscovered++;

                                    // Use HeardAbout() - adds to live bucket if room (like libtorrent)
                                    _routingTable.HeardAbout(newNode.Id, newNode.EndPoint);
                                }
                            }
                        }
                        else if (response == null)
                        {
                            _logger?.LogDebug("[FIND_NODE] Timeout from {Endpoint}", node?.EndPoint);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug("[FIND_NODE] Error processing response: {Message}", ex.Message);
                    }
                }

                _logger?.LogInformation("[FIND_NODE] Iteration {Iteration}: {Responses} responses, {Nodes} new nodes",
                    iterations, iterationResponses, iterationNodes);

                if (!foundCloser && toQuery.All(n => queried.Contains($"{n.Address}:{n.Port}")))
                {
                    _logger?.LogInformation("[FIND_NODE] No closer nodes found, ending");
                    break;
                }
            }

            var result = closest.Values.Take(DhtConstants.BucketSize).ToList();
            _logger?.LogInformation("[FIND_NODE] === COMPLETED find_node: discovered {Total} nodes, returning {Result} closest ===",
                totalNodesDiscovered, result.Count);

            // Log routing table state after find_node
            var rtStatsAfter = _routingTable.GetStats();
            _logger?.LogInformation("[FIND_NODE] Routing table after: {Live} live, {Replacement} replacement, {Buckets} buckets",
                rtStatsAfter.LiveNodes, rtStatsAfter.ReplacementNodes, rtStatsAfter.NumBuckets);

            NodesFound?.Invoke(result);
            return result;
        }

        private async Task<(NodeEntry node, DhtMessage response)> QueryFindNodeAsync(
            IPEndPoint target, NodeId targetId, CancellationToken cancellationToken)
        {
            var query = DhtMessage.CreateFindNodeQuery(_rpcManager.GenerateTransactionId(), NodeId, targetId, readOnly: _dhtMonitor.CurrentValue.ReadOnly);

            try
            {
                var response = await SendQueryAsync(query, target, cancellationToken);
                return (new NodeEntry(response?.NodeId ?? NodeId.Zero, target), response);
            }
            catch (OperationCanceledException)
            {
                return (null, null);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "find_node query to {Target} failed", target);
                return (null, null);
            }
        }

        /// <summary>
        /// Looks up peers for an info_hash.
        /// </summary>
        public async Task<List<EndPoint>> GetPeersAsync(byte[] infoHash, CancellationToken cancellationToken = default)
        {
            if (infoHash == null || infoHash.Length != 20)
                throw new ArgumentException("info_hash must be 20 bytes", nameof(infoHash));

            var infoHashHex = Convert.ToHexString(infoHash);
            _logger?.LogInformation("[GET_PEERS] === Starting get_peers for {InfoHash} ===", infoHashHex);

            var target = new NodeId(infoHash);
            var peers = new HashSet<EndPoint>();
            var nodesForAnnounce = new List<(NodeEntry node, byte[] token)>();

            var closest = new SortedDictionary<NodeId, NodeEntry>(
                Comparer<NodeId>.Create((a, b) =>
                    NodeId.Distance(a, target).CompareTo(NodeId.Distance(b, target))));

            var queried = new HashSet<string>();

            // Log routing table state before lookup
            var rtStats = _routingTable.GetStats();
            _logger?.LogInformation("[GET_PEERS] Routing table state: {LiveNodes} live, {ReplacementNodes} replacement, {Buckets} buckets, {RouterNodes} router nodes",
                rtStats.LiveNodes, rtStats.ReplacementNodes, rtStats.NumBuckets, rtStats.RouterNodes);

            // Start with closest known nodes - include replacement nodes if we have few live nodes
            // This is critical for bootstrapping when live buckets are sparse
            bool needsReplacements = rtStats.LiveNodes < DhtConstants.BucketSize;
            var initialNodes = _routingTable.FindClosestNodes(target, DhtConstants.BucketSize * 2,
                includeQuestionable: true, includeReplacements: needsReplacements);

            _logger?.LogInformation("[GET_PEERS] Found {Count} initial nodes from routing table (includeReplacements={IncludeRepl})",
                initialNodes.Count, needsReplacements);
            foreach (var node in initialNodes)
            {
                closest[node.Id] = node;
                _logger?.LogDebug("[GET_PEERS] Initial node: {NodeId} @ {Endpoint} (Pinged={Pinged}, Confirmed={Confirmed})",
                    node.Id.ToShortHex(), node.EndPoint, node.Pinged, node.IsConfirmed);
            }

            // If we have fewer than 3 nodes (libtorrent threshold), also add router nodes
            // This helps bootstrap when the routing table is very sparse
            if (initialNodes.Count < 3)
            {
                _logger?.LogWarning("[GET_PEERS] Very few nodes ({Count}), adding router nodes to help bootstrap", initialNodes.Count);
                foreach (var router in _routingTable.RouterNodes)
                {
                    // Use a unique placeholder ID for each router to avoid collisions
                    var routerId = NodeId.GenerateRandom();
                    NodeEntry routerNode = router is IPEndPoint ipRouter2
                        ? new NodeEntry(routerId, ipRouter2)
                        : new NodeEntry(routerId, router, 0);
                    if (!closest.Values.Any(n => n.NetworkEndPoint.Equals(router)))
                    {
                        closest[routerId] = routerNode;
                        _logger?.LogInformation("[GET_PEERS] Using router node: {Endpoint}", router);
                    }
                }
            }

            int iterations = 0;
            int maxIterations = 20;
            int totalQueriesSent = 0;
            int totalResponsesReceived = 0;
            int totalNodesDiscovered = 0;

            // Dynamic branch factor - start with configured value, increase if we're not getting responses
            int branchFactor = _dhtMonitor.CurrentValue.SearchBranching;
            int consecutiveEmptyIterations = 0;

            while (iterations++ < maxIterations && !cancellationToken.IsCancellationRequested)
            {
                // Find unqueried nodes - use dynamic branch factor
                var toQuery = closest.Values
                    .Where(n => !queried.Contains($"{n.Address}:{n.Port}"))
                    .Take(branchFactor)
                    .ToList();

                _logger?.LogInformation("[GET_PEERS] Iteration {Iteration}: {ToQueryCount} nodes to query, {ClosestCount} total in closest set, {QueriedCount} already queried",
                    iterations, toQuery.Count, closest.Count, queried.Count);

                if (toQuery.Count == 0)
                {
                    _logger?.LogInformation("[GET_PEERS] No more nodes to query, ending traversal");
                    break;
                }

                var tasks = new List<Task<(NodeEntry node, DhtMessage response)>>();
                foreach (var node in toQuery)
                {
                    string key = $"{node.Address}:{node.Port}";
                    queried.Add(key);
                    totalQueriesSent++;

                    _logger?.LogDebug("[GET_PEERS] Querying node {NodeId} @ {Endpoint}",
                        node.Id.ToShortHex(), node.EndPoint);
                    tasks.Add(QueryGetPeersAsync(node.EndPoint, infoHash, cancellationToken));
                }

                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug("[GET_PEERS] Task.WhenAll exception (some queries may have timed out): {Message}", ex.Message);
                }

                int iterationPeers = 0;
                int iterationNodes = 0;
                int iterationResponses = 0;

                foreach (var task in tasks)
                {
                    try
                    {
                        var (node, response) = await task;
                        if (response == null)
                        {
                            _logger?.LogDebug("[GET_PEERS] No response (timeout) from {Endpoint}",
                                node?.EndPoint ?? new IPEndPoint(IPAddress.None, 0));
                            continue;
                        }

                        iterationResponses++;
                        totalResponsesReceived++;
                        _logger?.LogDebug("[GET_PEERS] Got response from {Endpoint}: NodeId={NodeId}, HasValues={HasValues}, HasNodes={HasNodes}, HasToken={HasToken}",
                            response.SourceEndpoint,
                            response.NodeId.IsZero() ? "null" : response.NodeId.ToShortHex(),
                            response.Values != null && response.Values.Count > 0,
                            response.Nodes != null && response.Nodes.Length > 0,
                            response.Token != null);

                        // IMPORTANT: Mark the responding node as "seen" with Pinged=true
                        // This promotes it from replacement to live bucket and helps build the routing table
                        if (!response.NodeId.IsZero() && node != null)
                        {
                            _routingTable.NodeSeen(response.NodeId, node.EndPoint, 0);
                            _logger?.LogDebug("[GET_PEERS] Marked node {NodeId} as seen (promoting to live bucket)", response.NodeId.ToShortHex());
                        }

                        // Store token for announce
                        if (response.Token != null && node != null)
                        {
                            nodesForAnnounce.Add((node, response.Token));
                            _logger?.LogDebug("[GET_PEERS] Stored token from {Endpoint} for announce", node.EndPoint);
                        }

                        // Collect peers
                        if (response.Values != null && response.Values.Count > 0)
                        {
                            _logger?.LogInformation("[GET_PEERS] *** FOUND {Count} PEER ENTRIES in response from {Endpoint} ***",
                                response.Values.Count, response.SourceEndpoint);
                            var foundPeers = ParseCompactPeersViaTransport(response.Values);
                            _logger?.LogInformation("[GET_PEERS] Parsed {Count} valid peers", foundPeers.Count);
                            foreach (var peer in foundPeers)
                            {
                                if (peers.Add(peer))
                                {
                                    iterationPeers++;
                                    _logger?.LogInformation("[GET_PEERS] New peer discovered: {Peer}", peer);
                                }
                            }
                        }

                        // Add new nodes - use HeardAbout() like libtorrent's traverse()
                        // This adds nodes to the routing table immediately (to live bucket if room)
                        if (response.Nodes != null && response.Nodes.Length > 0)
                        {
                            var nodes = ParseCompactNodesViaTransport(response.Nodes);
                            _logger?.LogDebug("[GET_PEERS] Response contains {Count} nodes ({Bytes} bytes)",
                                nodes.Count, response.Nodes.Length);
                            foreach (var newNode in nodes)
                            {
                                if (!closest.ContainsKey(newNode.Id))
                                {
                                    closest[newNode.Id] = newNode;
                                    iterationNodes++;
                                    totalNodesDiscovered++;

                                    // Use HeardAbout() - adds to live bucket if room (like libtorrent)
                                    _routingTable.HeardAbout(newNode.Id, newNode.EndPoint);
                                    _logger?.LogDebug("[GET_PEERS] New node heard about: {NodeId} @ {Endpoint}",
                                        newNode.Id.ToShortHex(), newNode.EndPoint);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning("[GET_PEERS] Error processing response: {Message}", ex.Message);
                    }
                }

                _logger?.LogInformation("[GET_PEERS] Iteration {Iteration} results: {Responses} responses, {Peers} new peers, {Nodes} new nodes. Total peers so far: {TotalPeers}",
                    iterations, iterationResponses, iterationPeers, iterationNodes, peers.Count);

                // Dynamic branch factor adjustment (like libtorrent's short timeout behavior)
                // If we're not getting responses, increase concurrency to compensate
                if (iterationResponses == 0)
                {
                    consecutiveEmptyIterations++;
                    if (consecutiveEmptyIterations >= 2 && branchFactor < DhtConstants.BucketSize)
                    {
                        branchFactor = Math.Min(branchFactor + 2, DhtConstants.BucketSize);
                        _logger?.LogInformation("[GET_PEERS] No responses, increasing branch factor to {BranchFactor}", branchFactor);
                    }
                }
                else
                {
                    consecutiveEmptyIterations = 0;
                    // If we're getting good responses, we can reduce branch factor back to normal
                    if (iterationResponses >= branchFactor / 2 && branchFactor > _dhtMonitor.CurrentValue.SearchBranching)
                    {
                        branchFactor = Math.Max(branchFactor - 1, _dhtMonitor.CurrentValue.SearchBranching);
                    }
                }

                // If we found enough peers, stop
                if (peers.Count >= _dhtMonitor.CurrentValue.MaxPeersReply)
                {
                    _logger?.LogInformation("[GET_PEERS] Found enough peers ({Count}), stopping traversal", peers.Count);
                    break;
                }
            }

            var result = peers.ToList<EndPoint>();

            _logger?.LogInformation("[GET_PEERS] === COMPLETED get_peers for {InfoHash} ===", infoHashHex);
            _logger?.LogInformation("[GET_PEERS] Summary: {Queries} queries sent, {Responses} responses received, {NodesDiscovered} nodes discovered, {PeersFound} peers found",
                totalQueriesSent, totalResponsesReceived, totalNodesDiscovered, result.Count);

            PeersFound?.Invoke(infoHash, result);
            return result;
        }

        private async Task<(NodeEntry node, DhtMessage response)> QueryGetPeersAsync(
            IPEndPoint target, byte[] infoHash, CancellationToken cancellationToken)
        {
            var query = DhtMessage.CreateGetPeersQuery(_rpcManager.GenerateTransactionId(), NodeId, infoHash, scrape: true, readOnly: _dhtMonitor.CurrentValue.ReadOnly);

            try
            {
                var response = await SendQueryAsync(query, target, cancellationToken);
                return (response != null ? new NodeEntry(response.NodeId, target) : null, response);
            }
            catch (OperationCanceledException)
            {
                return (null, null);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "get_peers query to {Target} failed", target);
                return (null, null);
            }
        }

        /// <summary>
        /// Announces that we have a torrent to the DHT.
        /// </summary>
        public async Task AnnounceAsync(byte[] infoHash, int port, CancellationToken cancellationToken = default)
        {
            if (infoHash == null || infoHash.Length != 20)
                throw new ArgumentException("info_hash must be 20 bytes", nameof(infoHash));

            _logger?.LogDebug("Starting announce for {InfoHash} on port {Port}",
                Convert.ToHexString(infoHash), port);

            // First do get_peers to find closest nodes and get tokens
            var target = new NodeId(infoHash);
            var nodesWithTokens = new List<(NodeEntry node, byte[] token)>();

            var closest = _routingTable.FindClosestNodes(target, DhtConstants.BucketSize * 2, true);
            var queried = new HashSet<string>();

            foreach (var node in closest.Take(DhtConstants.BucketSize))
            {
                string key = $"{node.Address}:{node.Port}";
                if (queried.Contains(key)) continue;
                queried.Add(key);

                try
                {
                    var response = await QueryGetPeersAsync(node.EndPoint, infoHash, cancellationToken);
                    if (response.response?.Token != null)
                    {
                        nodesWithTokens.Add((node, response.response.Token));
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to get token from {Node} for announce", node.EndPoint);
                }
            }

            // Announce to all nodes we got tokens from
            var announceTasks = nodesWithTokens.Select(async n =>
            {
                var query = DhtMessage.CreateAnnouncePeerQuery(
                    _rpcManager.GenerateTransactionId(),
                    NodeId,
                    infoHash,
                    port,
                    n.token,
                    readOnly: _dhtMonitor.CurrentValue.ReadOnly);

                try
                {
                    await SendQueryAsync(query, n.node.EndPoint, cancellationToken);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to announce to {Node}", n.node.EndPoint);
                    return false;
                }
            });

            var results = await Task.WhenAll(announceTasks);
            int successCount = results.Count(r => r);

            _logger?.LogDebug("Announced {InfoHash} to {SuccessCount}/{TotalCount} nodes",
                Convert.ToHexString(infoHash), successCount, nodesWithTokens.Count);
        }

        /// <summary>
        /// BEP 51: Sends a sample_infohashes query to a specific node.
        /// This is a single-shot query (not an iterative traversal), matching libtorrent's approach.
        /// </summary>
        public async Task<SampleInfohashesResult> SampleInfohashesAsync(
            IPEndPoint target, NodeId targetId, CancellationToken ct)
        {
            var result = new SampleInfohashesResult { MinIntervalSeconds = int.MaxValue };

            try
            {
                var query = DhtMessage.CreateSampleInfohashesQuery(
                    _rpcManager.GenerateTransactionId(), NodeId, targetId,
                    readOnly: _dhtMonitor.CurrentValue.ReadOnly);

                var response = await SendQueryAsync(query, target, ct);

                if (response?.Samples != null && response.Samples.Length > 0)
                {
                    if (response.Samples.Length % 20 != 0)
                    {
                        _logger?.LogWarning("[SAMPLE_INFOHASHES] Invalid samples length {Length} from {Target}",
                            response.Samples.Length, target);
                        return result;
                    }

                    for (int i = 0; i + 20 <= response.Samples.Length; i += 20)
                    {
                        var hash = new byte[20];
                        Array.Copy(response.Samples, i, hash, 0, 20);
                        result.Infohashes.Add(hash);
                    }
                }

                if (response != null && !response.NodeId.IsZero())
                    result.NodeTotals[response.NodeId] = response.SampleNum;

                int interval = response?.SampleInterval ?? 0;
                if (interval >= 0 && interval <= 21600)
                    result.MinIntervalSeconds = Math.Min(result.MinIntervalSeconds, interval);

                _logger?.LogDebug("[SAMPLE_INFOHASHES] Got {Count} samples, num={Num}, interval={Interval} from {Target}",
                    result.Infohashes.Count, response?.SampleNum, interval, target);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("[SAMPLE_INFOHASHES] Query to {Target} cancelled", target);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[SAMPLE_INFOHASHES] Query to {Target} failed", target);
            }

            if (result.MinIntervalSeconds == int.MaxValue)
                result.MinIntervalSeconds = 0;

            return result;
        }

        /// <summary>
        /// Sends a query and waits for response.
        /// </summary>
        private async Task<DhtMessage> SendQueryAsync(DhtMessage query, IPEndPoint target,
            CancellationToken cancellationToken)
        {
            var data = query.Encode();

            var responseTask = _rpcManager.RegisterQueryAsync(query, target, cancellationToken);

            _logger?.LogInformation("[SEND] Sending {QueryType} query ({Size} bytes) to {Target}, TxId={TxId}",
                query.QueryType, data.Length, target, Convert.ToHexString(query.TransactionId));

            try
            {
                await _transport.SendAsync(data, target, cancellationToken).ConfigureAwait(false);
                _logger?.LogDebug("[SEND] Sent {Bytes} bytes to {Target}", data.Length, target);
            }
            catch (SocketException ex)
            {
                _logger?.LogError(ex, "[SEND] Failed to send query to {Target} - socket error", target);
                return null;
            }

            var response = await responseTask;

            if (response != null)
            {
                _logger?.LogInformation("[SEND] Got response from {Target} for TxId={TxId}",
                    target, Convert.ToHexString(query.TransactionId));
            }
            else
            {
                _logger?.LogWarning("[SEND] TIMEOUT from {Target} for TxId={TxId} after {Timeout}ms",
                    target, Convert.ToHexString(query.TransactionId), _dhtMonitor.CurrentValue.QueryTimeoutMs);
            }

            return response;
        }

        private void ProcessIncomingPacket(ReadOnlyMemory<byte> data, EndPoint sender)
        {
            if (!_isRunning) return;

            if (sender is IPEndPoint ipSender && !_dosBlocker.RecordPacket(ipSender.Address))
            {
                _logger?.LogWarning("[RECEIVE] Rate-limited packet from {Source}", sender);
                return;
            }

            try
            {
                HandleIncomingMessage(data.ToArray(), sender);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[RECEIVE] Error handling message from {Source}", sender);
            }
        }

        /// <summary>
        /// Handles an incoming message.
        /// </summary>
        private void HandleIncomingMessage(byte[] data, EndPoint source)
        {
            try
            {
                _logger?.LogDebug("[HANDLE] Received {Size} bytes from {Source}", data.Length, source);

                var ipSource = source as IPEndPoint;
                var message = DhtMessage.Parse(data, ipSource);

                _logger?.LogInformation("[HANDLE] Parsed {Type} message from {Source}, TxId={TxId}",
                    message.MessageType, source, Convert.ToHexString(message.TransactionId));

                switch (message.MessageType)
                {
                    case DhtMessageType.Query:
                        HandleQuery(message, source);
                        break;

                    case DhtMessageType.Response:
                        _logger?.LogDebug("[HANDLE] Passing response to RPC manager for TxId={TxId}",
                            Convert.ToHexString(message.TransactionId));
                        _rpcManager.HandleResponse(message);
                        break;

                    case DhtMessageType.Error:
                        _logger?.LogWarning("[HANDLE] Received error response from {Source}: {Error}",
                            source, message.ErrorMessage ?? "unknown");
                        _rpcManager.HandleResponse(message);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[HANDLE] Failed to parse message from {Source}. Raw data (first 100 bytes): {Data}",
                    source, Convert.ToHexString(data.AsSpan(0, Math.Min(100, data.Length))));
            }
        }

        /// <summary>
        /// Handles an incoming query.
        /// </summary>
        private void HandleQuery(DhtMessage query, EndPoint source)
        {
            // BEP 43: Read-only nodes don't respond to queries
            if (_dhtMonitor.CurrentValue.ReadOnly)
            {
                _logger?.LogDebug("[HANDLE_QUERY] Ignoring {QueryType} from {Source} — read-only mode", query.QueryType, source);
                return;
            }

            _logger?.LogInformation("[HANDLE_QUERY] Received {QueryType} query from {Source}, TxId={TxId}",
                query.QueryType, source, Convert.ToHexString(query.TransactionId));

            var ipSource = source as IPEndPoint;

            // BEP-42: Verify node ID if enforcement is enabled
            bool isVerifiedId = true;
            if (!query.NodeId.IsZero())
            {
                if (ipSource != null && _dhtMonitor.CurrentValue.EnforceNodeId)
                {
                    isVerifiedId = NodeId.VerifyId(query.NodeId, ipSource.Address);
                    if (!isVerifiedId)
                    {
                        _logger?.LogDebug("Node ID verification failed for {Source} per BEP-42", source);
                    }
                }

                // BEP 43: Don't add read-only nodes to routing table (they won't respond to pings)
                if (!query.ReadOnly && ipSource != null)
                {
                    var nodeEntry = new NodeEntry(query.NodeId, ipSource);
                    nodeEntry.Verified = isVerifiedId;
                    _routingTable.AddNode(nodeEntry);
                }
                else if (query.ReadOnly)
                {
                    _logger?.LogDebug("[HANDLE_QUERY] Skipping routing table add for read-only node {Source}", source);
                }
            }

            DhtMessage response;

            switch (query.QueryType)
            {
                case DhtQueryType.Ping:
                    _logger?.LogDebug("[HANDLE_QUERY] Responding to ping from {Source}", source);
                    response = DhtMessage.CreatePingResponse(query.TransactionId, NodeId);
                    break;

                case DhtQueryType.FindNode:
                    var closestNodes = _routingTable.FindClosestNodes(query.Target, DhtConstants.BucketSize);
                    var nodesBytes = EncodeCompactNodesViaTransport(closestNodes);
                    _logger?.LogDebug("[HANDLE_QUERY] find_node: returning {Count} nodes ({Bytes} bytes) for target {Target}",
                        closestNodes.Count, nodesBytes.Length, query.Target.ToShortHex());
                    response = DhtMessage.CreateFindNodeResponse(query.TransactionId, NodeId, nodesBytes);
                    break;

                case DhtQueryType.GetPeers:
                    var infoHashHex = Convert.ToHexString(query.InfoHash);
                    _logger?.LogInformation("[HANDLE_QUERY] get_peers query for info_hash={InfoHash} from {Source}",
                        infoHashHex, source);

                    // BEP 33: When scrape is requested, reduce max peers to fit bloom filters under UDP MTU
                    int maxPeersForResponse = query.Scrape
                        ? Math.Min(_dhtMonitor.CurrentValue.MaxPeersReply, 131) // (1400 - 512 filters - 100 overhead) / 6
                        : _dhtMonitor.CurrentValue.MaxPeersReply;

                    var token = _tokenManager.GenerateToken(ipSource?.Address ?? IPAddress.None, query.InfoHash);
                    var peers = _storage.GetPeers(query.InfoHash, maxPeersForResponse);

                    if (peers.Count > 0)
                    {
                        _logger?.LogInformation("[HANDLE_QUERY] *** We have {PeerCount} peers for {InfoHash} ***",
                            peers.Count, infoHashHex);
                        foreach (var peer in peers)
                        {
                            _logger?.LogDebug("[HANDLE_QUERY] Returning peer: {Peer}", peer);
                        }
                        var peerBytes = peers.Select(DhtMessage.EncodeCompactPeer).ToList();
                        var nodes = _routingTable.FindClosestNodes(new NodeId(query.InfoHash), DhtConstants.BucketSize);
                        _logger?.LogDebug("[HANDLE_QUERY] Also returning {NodeCount} close nodes", nodes.Count);
                        response = DhtMessage.CreateGetPeersResponseWithPeers(
                            query.TransactionId, NodeId, token, peerBytes,
                            EncodeCompactNodesViaTransport(nodes));
                    }
                    else
                    {
                        var nodes = _routingTable.FindClosestNodes(new NodeId(query.InfoHash), DhtConstants.BucketSize);
                        _logger?.LogDebug("[HANDLE_QUERY] No peers for {InfoHash}, returning {NodeCount} close nodes",
                            infoHashHex, nodes.Count);
                        foreach (var node in nodes)
                        {
                            _logger?.LogDebug("[HANDLE_QUERY] Returning node: {NodeId} @ {Endpoint}",
                                node.Id.ToShortHex(), node.EndPoint);
                        }
                        response = DhtMessage.CreateGetPeersResponseWithNodes(
                            query.TransactionId, NodeId, token,
                            EncodeCompactNodesViaTransport(nodes));
                    }

                    // BEP 33: Attach bloom filters if scrape was requested
                    if (query.Scrape && _storage.HasPeers(query.InfoHash))
                    {
                        if (!query.NoSeed)
                            response.BFsd = _storage.GetSeedBloomFilter(query.InfoHash).Data.ToArray();
                        response.BFpe = _storage.GetPeerBloomFilter(query.InfoHash).Data.ToArray();
                    }

                    break;

                case DhtQueryType.AnnouncePeer:
                    var announceInfoHash = Convert.ToHexString(query.InfoHash);
                    _logger?.LogInformation("[HANDLE_QUERY] announce_peer for {InfoHash} from {Source}, port={Port}, implied={Implied}",
                        announceInfoHash, source, query.Port, query.ImpliedPort);

                    if (!_tokenManager.ValidateToken(query.Token, ipSource?.Address ?? IPAddress.None, query.InfoHash))
                    {
                        _logger?.LogWarning("[HANDLE_QUERY] Invalid token for announce_peer from {Source}", source);
                        response = DhtMessage.CreateErrorResponse(
                            query.TransactionId, DhtErrorCode.ProtocolError, "Invalid token");
                        break;
                    }

                    int peerPort = query.ImpliedPort ? (ipSource?.Port ?? 0) : query.Port;
                    var announceAddress = ipSource?.Address ?? IPAddress.None;
                    _storage.AnnouncePeer(query.InfoHash, new IPEndPoint(announceAddress, peerPort), query.IsSeed);
                    _logger?.LogInformation("[HANDLE_QUERY] Stored peer {IP}:{Port} for {InfoHash}",
                        announceAddress, peerPort, announceInfoHash);

                    response = DhtMessage.CreateAnnouncePeerResponse(query.TransactionId, NodeId);
                    break;

                case DhtQueryType.SampleInfohashes:
                    _logger?.LogInformation("[HANDLE_QUERY] sample_infohashes from {Source}, target={Target}",
                        source, query.Target.IsZero() ? "null" : query.Target.ToShortHex());

                    var sampleTargetId = query.Target.IsZero() ? NodeId : query.Target;
                    var sampleNodes = _routingTable.FindClosestNodes(
                        sampleTargetId, DhtConstants.BucketSize);
                    var sampleNodesBytes = EncodeCompactNodesViaTransport(sampleNodes);
                    var sampleResult = _storage.GetInfohashesSample();

                    response = DhtMessage.CreateSampleInfohashesResponse(
                        query.TransactionId, NodeId, sampleNodesBytes,
                        sampleResult.Samples, sampleResult.TotalCount,
                        sampleResult.IntervalSeconds);
                    break;

                default:
                    _logger?.LogWarning("[HANDLE_QUERY] Unknown query type from {Source}", source);
                    response = DhtMessage.CreateErrorResponse(
                        query.TransactionId, DhtErrorCode.MethodUnknown, "Unknown query type");
                    break;
            }

            SendResponse(response, source);
        }

        private void SendResponse(DhtMessage response, EndPoint target)
        {
            try
            {
                var data = response.Encode();
                // Fire-and-forget for response sends
                _ = _transport.SendAsync(data, target);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to send response to {Target}", target);
            }
        }

        private void OnQueryTimedOut(PendingQuery query)
        {
            _routingTable.NodeFailed(query.Query.NodeId, query.Target);
        }

        private void OnMaintenanceTick(object state)
        {
            if (!_isRunning) return;

            try
            {
                var stats = _routingTable.GetStats();

                // Like libtorrent: use find_node for bucket refresh when buckets aren't full
                // This discovers new nodes instead of just verifying existing ones
                int bucketToRefresh = _routingTable.GetBucketNeedingRefresh();
                if (bucketToRefresh >= 0)
                {
                    bool bucketHasRoom = _routingTable.BucketHasRoom(bucketToRefresh);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (bucketHasRoom)
                            {
                                // Bucket has room - use find_node to discover new nodes
                                // Generate a random target ID that would fall into this bucket
                                var target = _routingTable.GenerateRandomIdForBucket(bucketToRefresh);
                                _logger?.LogDebug("[MAINTENANCE] Bucket {Bucket} has room, running find_node for {Target}",
                                    bucketToRefresh, target.ToShortHex());
                                await FindNodeAsync(target, CancellationToken.None);
                            }
                            else
                            {
                                // Bucket is full - just ping the oldest node to verify it's alive
                                var candidate = _routingTable.GetNextRefreshCandidate();
                                if (candidate != null)
                                {
                                    _logger?.LogDebug("[MAINTENANCE] Pinging {Node} for refresh",
                                        candidate.EndPoint);
                                    await PingAsync(candidate.EndPoint, CancellationToken.None);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug(ex, "[MAINTENANCE] Bucket refresh operation failed for bucket {Bucket}", bucketToRefresh);
                        }
                    });
                }
                else
                {
                    // No bucket needs refresh, but still check for nodes needing ping
                    var candidate = _routingTable.GetNextRefreshCandidate();
                    if (candidate != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await PingAsync(candidate.EndPoint, CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug(ex, "[MAINTENANCE] Ping to {Node} failed during refresh", candidate.EndPoint);
                            }
                        });
                    }
                }

                // Periodic bootstrap if routing table is small
                if (stats.LiveNodes < 10 && (DateTime.UtcNow - _lastBootstrap).TotalMinutes >= 5)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await BootstrapAsync(CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug(ex, "[MAINTENANCE] Periodic bootstrap failed");
                        }
                    });
                }

                // Cleanup storage
                _storage.Cleanup();

                // Cleanup DoS blocker stale entries
                _dosBlocker.Cleanup();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in maintenance tick");
            }
        }

        /// <summary>
        /// Gets statistics about the DHT node.
        /// </summary>
        public DhtNodeStats GetStats()
        {
            var routingStats = _routingTable.GetStats();
            var storageStats = _storage.GetStats();

            return new DhtNodeStats
            {
                NodeId = NodeId,
                IsRunning = _isRunning,
                NumBuckets = routingStats.NumBuckets,
                LiveNodes = routingStats.LiveNodes,
                ReplacementNodes = routingStats.ReplacementNodes,
                ConfirmedNodes = routingStats.ConfirmedNodes,
                RouterNodes = routingStats.RouterNodes,
                PendingQueries = _rpcManager.PendingCount,
                StoredInfoHashes = storageStats.InfoHashCount,
                StoredPeers = storageStats.TotalPeerCount,
                TrackedIps = _dosBlocker.TrackedIpCount,
                BlockedIps = _dosBlocker.BlockedIpCount
            };
        }

        /// <summary>
        /// Parses compact peer values from a get_peers response, using the transport's network format.
        /// For clearnet (CompactNodeInfoSize==26): each peer value is 6 bytes (IPv4:port).
        /// For I2P (CompactNodeInfoSize==54): each peer value is 32 bytes (destination hash).
        /// </summary>
        private List<EndPoint> ParseCompactPeersViaTransport(List<byte[]> peerData)
        {
            // For clearnet: compact node info is 26 bytes (20 nodeId + 4 IP + 2 port)
            // Peer values are 6-byte IPv4:port — use the existing parser
            if (_transport.CompactNodeInfoSize == 26)
            {
                return DhtMessage.ParseCompactPeers(peerData).Cast<EndPoint>().ToList();
            }

            // For I2P: each peer value is a 32-byte destination hash
            var result = new List<EndPoint>();
            foreach (var data in peerData)
            {
                if (data != null && data.Length >= I2pDestination.HashLength)
                {
                    try
                    {
                        var dest = I2pDestination.FromHash(data.AsSpan(0, I2pDestination.HashLength).ToArray());
                        result.Add(new I2pEndPoint(dest));
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug("[GET_PEERS] Failed to parse I2P peer hash: {Message}", ex.Message);
                    }
                }
            }
            return result;
        }

        private List<NodeEntry> ParseCompactNodesViaTransport(byte[] data)
        {
            var size = _transport.CompactNodeInfoSize;
            if (data == null || data.Length == 0 || data.Length % size != 0)
                return new List<NodeEntry>();

            var count = data.Length / size;
            var result = new List<NodeEntry>(count);
            for (int i = 0; i < count; i++)
            {
                var (nodeId, endpoint, port) = _transport.DecodeCompactNodeInfo(data, i * size);
                result.Add(new NodeEntry(new NodeId(nodeId), endpoint, port));
            }
            return result;
        }

        private byte[] EncodeCompactNodesViaTransport(IReadOnlyList<NodeEntry> nodes)
        {
            var size = _transport.CompactNodeInfoSize;
            var result = new byte[nodes.Count * size];
            for (int i = 0; i < nodes.Count; i++)
            {
                var encoded = _transport.EncodeCompactNodeInfo(nodes[i]);
                encoded.CopyTo(result.AsSpan(i * size));
            }
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _rpcManager?.Dispose();
            _transport.Dispose();
        }
    }
}
