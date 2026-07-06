using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Storage;
using vTorrent.Abstractions.Records;

namespace vTorrent.Core.DHT
{
    /// <summary>
    /// Represents the persistent state of the DHT node.
    /// Similar to libtorrent's dht_state structure, this stores:
    /// - Our node ID
    /// - Known good nodes for bootstrap
    /// </summary>
    public class DhtPersistedState
    {
        /// <summary>
        /// Our node's ID (hex string, 40 chars).
        /// </summary>
        [JsonPropertyName("node_id")]
        public string NodeIdHex { get; set; }

        /// <summary>
        /// List of known nodes for bootstrap.
        /// </summary>
        [JsonPropertyName("nodes")]
        public List<DhtNodeInfo> Nodes { get; set; } = new();

        /// <summary>
        /// When this state was last saved.
        /// </summary>
        [JsonPropertyName("saved_at")]
        public DateTime SavedAt { get; set; }

        /// <summary>
        /// Version of the state format.
        /// </summary>
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;
    }

    /// <summary>
    /// Represents a single DHT node for persistence.
    /// </summary>
    public class DhtNodeInfo
    {
        /// <summary>
        /// Node ID in hex format (40 chars).
        /// </summary>
        [JsonPropertyName("id")]
        public string NodeIdHex { get; set; }

        /// <summary>
        /// IP address.
        /// </summary>
        [JsonPropertyName("ip")]
        public string IpAddress { get; set; }

        /// <summary>
        /// UDP port.
        /// </summary>
        [JsonPropertyName("port")]
        public int Port { get; set; }

        /// <summary>
        /// Last known round-trip time in ms.
        /// </summary>
        [JsonPropertyName("rtt")]
        public int RttMs { get; set; }
    }

    /// <summary>
    /// Manages saving and loading DHT state for persistence across sessions.
    /// Based on libtorrent's pattern where nodes are saved to enable faster bootstrap.
    /// Uses SQLite via TorrentDatabase for storage.
    /// </summary>
    public class DhtStatePersistence
    {
        private readonly TorrentDatabase _database;
        private readonly ILogger _logger;

        /// <summary>
        /// Maximum number of nodes to persist.
        /// </summary>
        public int MaxNodesToSave { get; set; } = 400;

        public DhtStatePersistence(TorrentDatabase database, ILogger logger = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _logger = logger;
        }

        /// <summary>
        /// Saves the current DHT state to the database.
        /// </summary>
        public async Task SaveStateAsync(NodeId ourNodeId, RoutingTable routingTable)
        {
            if (ourNodeId == null || routingTable == null)
            {
                _logger?.LogWarning("Cannot save DHT state: invalid parameters");
                return;
            }

            try
            {
                // Save node ID
                await _database.SaveDhtStateAsync("node_id", ourNodeId.ToString());

                // Save timestamp as Unix epoch seconds
                var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await _database.SaveDhtStateAsync("last_saved", epoch.ToString());

                // Get all confirmed/good nodes from routing table
                var allNodes = routingTable.GetAllNodes();
                var records = new List<DhtNodeRecord>();

                foreach (var node in allNodes)
                {
                    if (records.Count >= MaxNodesToSave)
                        break;

                    // Only save confirmed (pinged and responsive) nodes
                    if (!node.IsConfirmed)
                        continue;

                    // Only save IPv4 nodes for now
                    if (node.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        continue;

                    records.Add(new DhtNodeRecord
                    {
                        NodeId = node.Id.ToString(),
                        Ip = node.Address.ToString(),
                        Port = node.Port,
                        RttMs = node.RttMs,
                        LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                }

                // Save nodes to database
                await _database.SaveDhtNodesAsync(records);

                _logger?.LogInformation("Saved DHT state with {Count} nodes to database", records.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save DHT state");
            }
        }

        /// <summary>
        /// Loads the DHT state from the database.
        /// </summary>
        public async Task<DhtPersistedState> LoadStateAsync()
        {
            try
            {
                // Load node ID
                var nodeIdHex = await _database.GetDhtStateAsync("node_id");
                if (string.IsNullOrEmpty(nodeIdHex))
                {
                    _logger?.LogDebug("No DHT node_id found in database");
                    return null;
                }

                // Load last_saved timestamp and check 7-day expiry
                var lastSavedStr = await _database.GetDhtStateAsync("last_saved");
                if (!string.IsNullOrEmpty(lastSavedStr) && long.TryParse(lastSavedStr, out var lastSavedEpoch))
                {
                    var lastSaved = DateTimeOffset.FromUnixTimeSeconds(lastSavedEpoch).UtcDateTime;
                    if ((DateTime.UtcNow - lastSaved).TotalDays > 7)
                    {
                        _logger?.LogInformation("DHT state is older than 7 days, will use fresh bootstrap");
                        return null;
                    }
                }

                // Load nodes from database (already excludes old ones)
                var nodeRecords = await _database.GetDhtNodesAsync(MaxNodesToSave);

                var state = new DhtPersistedState
                {
                    NodeIdHex = nodeIdHex,
                    SavedAt = !string.IsNullOrEmpty(lastSavedStr) && long.TryParse(lastSavedStr, out var ts)
                        ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime
                        : DateTime.UtcNow,
                    Version = 1,
                    Nodes = nodeRecords.Select(r => new DhtNodeInfo
                    {
                        NodeIdHex = r.NodeId,
                        IpAddress = r.Ip,
                        Port = r.Port,
                        RttMs = r.RttMs
                    }).ToList()
                };

                _logger?.LogInformation("Loaded DHT state with {Count} nodes from database",
                    state.Nodes.Count);

                return state;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load DHT state");
                return null;
            }
        }

        /// <summary>
        /// Converts persisted node info to NodeEntry objects.
        /// </summary>
        public List<NodeEntry> GetNodesFromState(DhtPersistedState state)
        {
            var result = new List<NodeEntry>();

            if (state?.Nodes == null)
                return result;

            foreach (var nodeInfo in state.Nodes)
            {
                try
                {
                    if (string.IsNullOrEmpty(nodeInfo.NodeIdHex) ||
                        string.IsNullOrEmpty(nodeInfo.IpAddress))
                        continue;

                    var nodeId = NodeId.FromHex(nodeInfo.NodeIdHex);
                    var ip = IPAddress.Parse(nodeInfo.IpAddress);
                    var entry = new NodeEntry(nodeId, ip, nodeInfo.Port, nodeInfo.RttMs, pinged: false);

                    result.Add(entry);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to parse node info: {Id}", nodeInfo.NodeIdHex);
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the stored node ID if available.
        /// </summary>
        public NodeId? GetStoredNodeId(DhtPersistedState state)
        {
            if (state == null || string.IsNullOrEmpty(state.NodeIdHex))
                return null;

            try
            {
                return NodeId.FromHex(state.NodeIdHex);
            }
            catch
            {
                return null;
            }
        }
    }
}
