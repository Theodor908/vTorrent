using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.DHT
{
    /// <summary>
    /// Kademlia routing table implementation based on BEP 5 and libtorrent patterns.
    /// Organizes nodes into k-buckets based on XOR distance from our node ID.
    /// </summary>
    public class RoutingTable
    {
        private readonly object _lock = new();
        private readonly List<RoutingBucket> _buckets;
        private readonly HashSet<IPAddress> _ips;
        private readonly HashSet<EndPoint> _routerNodes;
        private readonly IOptionsMonitor<DhtSettings> _dhtMonitor;

        private NodeId _id;
        private int _depth;

        /// <summary>
        /// Our node's ID.
        /// </summary>
        public NodeId Id => _id;

        /// <summary>
        /// Number of active buckets.
        /// </summary>
        public int NumBuckets
        {
            get { lock (_lock) return _buckets.Count; }
        }

        /// <summary>
        /// Event raised when a node is added to the routing table.
        /// </summary>
        public event Action<NodeEntry> NodeAdded;

        /// <summary>
        /// Event raised when a node is removed from the routing table.
        /// </summary>
        public event Action<NodeEntry> NodeRemoved;

        public RoutingTable(NodeId id, IOptionsMonitor<DhtSettings> dhtMonitor)
        {
            _id = id;
            _dhtMonitor = dhtMonitor ?? throw new ArgumentNullException(nameof(dhtMonitor));
            _buckets = new List<RoutingBucket>(32);
            _ips = new HashSet<IPAddress>();
            _routerNodes = new HashSet<EndPoint>();
            _depth = 0;

            // Start with one bucket covering the entire ID space
            _buckets.Add(new RoutingBucket(DhtConstants.BucketSize));
        }

        /// <summary>
        /// Adds a router node (bootstrap node that is never added to buckets).
        /// </summary>
        public void AddRouterNode(EndPoint endpoint)
        {
            lock (_lock)
            {
                _routerNodes.Add(endpoint);
            }
        }

        /// <summary>
        /// Gets all router nodes.
        /// </summary>
        public IReadOnlyCollection<EndPoint> RouterNodes
        {
            get
            {
                lock (_lock)
                {
                    return _routerNodes.ToList();
                }
            }
        }

        /// <summary>
        /// Gets the bucket size limit for a given bucket index.
        /// Extended routing tables have larger buckets closer to our ID.
        /// </summary>
        public int BucketLimit(int bucketIndex)
        {
            if (!_dhtMonitor.CurrentValue.ExtendedRoutingTable)
                return DhtConstants.BucketSize;

            // libtorrent pattern: first buckets are larger
            return bucketIndex switch
            {
                0 => DhtConstants.BucketSize * 16,
                1 => DhtConstants.BucketSize * 8,
                2 => DhtConstants.BucketSize * 4,
                3 => DhtConstants.BucketSize * 2,
                _ => DhtConstants.BucketSize
            };
        }

        /// <summary>
        /// Finds the appropriate bucket for a given node ID.
        /// </summary>
        private int FindBucketIndex(NodeId id)
        {
            int numBuckets = _buckets.Count;
            if (numBuckets == 0) return 0;

            int distExp = NodeId.DistanceExp(_id, id);
            int bucketIndex = Math.Min(NodeId.BitLength - 1 - distExp, numBuckets - 1);
            return Math.Max(0, bucketIndex);
        }

        /// <summary>
        /// Adds a node to the routing table. Returns true if the node was added.
        /// </summary>
        public bool AddNode(NodeEntry entry)
        {
            if (entry == null)
            {
                System.Diagnostics.Debug.WriteLine("[RT_ADD] Null entry, rejecting");
                return false;
            }
            if (entry.Id.Equals(_id))
            {
                System.Diagnostics.Debug.WriteLine($"[RT_ADD] Self node {entry.Id.ToShortHex()}, rejecting");
                return false;
            }

            lock (_lock)
            {
                System.Diagnostics.Debug.WriteLine($"[RT_ADD] Adding node {entry.Id.ToShortHex()} @ {entry.NetworkEndPoint} (Pinged={entry.Pinged})");

                // If this was a router node, promote it to a regular node now that we know its ID
                if (_routerNodes.Contains(entry.NetworkEndPoint))
                {
                    _routerNodes.Remove(entry.NetworkEndPoint);
                    System.Diagnostics.Debug.WriteLine($"[RT_ADD] Promoted from router node");
                }

                // Check IP restrictions
                if (entry.Address != null && _dhtMonitor.CurrentValue.RestrictRoutingIps && _ips.Contains(entry.Address))
                {
                    // IP already exists, check if it's the same node
                    var existing = FindNodeByEndpoint(entry.NetworkEndPoint);
                    if (existing != null)
                    {
                        if (existing.Id.Equals(entry.Id))
                        {
                            // Same node, update it
                            UpdateExistingNode(existing, entry);
                            System.Diagnostics.Debug.WriteLine($"[RT_ADD] Updated existing node");
                            return true;
                        }
                        else
                        {
                            // Different ID for same IP - potential attack, reject
                            System.Diagnostics.Debug.WriteLine($"[RT_ADD] Different ID for same IP, rejecting (potential attack)");
                            return false;
                        }
                    }
                    // Different port same IP - reject if restricting
                    System.Diagnostics.Debug.WriteLine($"[RT_ADD] IP restriction: different port same IP, rejecting");
                    return false;
                }

                var result = AddNodeImpl(entry);

                // Handle bucket splitting if needed
                while (result == AddNodeResult.NeedBucketSplit)
                {
                    if (_buckets.Count >= 50) // Sanity limit
                    {
                        System.Diagnostics.Debug.WriteLine($"[RT_ADD] Max buckets reached, cannot split further");
                        result = AddNodeImpl(entry);
                        break;
                    }

                    System.Diagnostics.Debug.WriteLine($"[RT_ADD] Splitting bucket, current count: {_buckets.Count}");
                    SplitBucket();
                    result = AddNodeImpl(entry);
                }

                System.Diagnostics.Debug.WriteLine($"[RT_ADD] Result: {result}");
                return result == AddNodeResult.Added;
            }
        }

        private void UpdateExistingNode(NodeEntry existing, NodeEntry newEntry)
        {
            existing.OnResponseReceived(newEntry.RttMs);
        }

        private enum AddNodeResult
        {
            Added,
            Failed,
            NeedBucketSplit
        }

        private AddNodeResult AddNodeImpl(NodeEntry entry)
        {
            int bucketIndex = FindBucketIndex(entry.Id);
            var bucket = _buckets[bucketIndex];
            int bucketLimit = BucketLimit(bucketIndex);

            System.Diagnostics.Debug.WriteLine($"[RT_IMPL] Bucket {bucketIndex}: {bucket.LiveCount}/{bucketLimit} live, {bucket.ReplacementCount} replacement");

            // Check if node already exists in bucket
            var existing = bucket.FindById(entry.Id);
            if (existing != null)
            {
                // Update existing node
                if (existing.NetworkEndPoint.Equals(entry.NetworkEndPoint))
                {
                    UpdateExistingNode(existing, entry);
                    System.Diagnostics.Debug.WriteLine($"[RT_IMPL] Updated existing live node");
                    return AddNodeResult.Added;
                }
                // Different endpoint for same ID - reject
                System.Diagnostics.Debug.WriteLine($"[RT_IMPL] Different endpoint for same ID, rejecting");
                return AddNodeResult.Failed;
            }

            // Check if node exists in replacement bucket
            var replacement = bucket.FindReplacementById(entry.Id);
            if (replacement != null)
            {
                if (replacement.NetworkEndPoint.Equals(entry.NetworkEndPoint))
                {
                    UpdateExistingNode(replacement, entry);
                    // Try to promote from replacement
                    TryPromoteFromReplacement(bucket, bucketLimit);
                    System.Diagnostics.Debug.WriteLine($"[RT_IMPL] Updated existing replacement node");
                    return AddNodeResult.Added;
                }
                System.Diagnostics.Debug.WriteLine($"[RT_IMPL] Different endpoint for same ID in replacement, rejecting");
                return AddNodeResult.Failed;
            }

            // If bucket has room, add directly
            if (entry.Pinged && bucket.LiveCount < bucketLimit)
            {
                bucket.AddLive(entry);
                if (entry.Address != null) _ips.Add(entry.Address);
                NodeAdded?.Invoke(entry);
                System.Diagnostics.Debug.WriteLine($"[RT_IMPL] Added to LIVE bucket (Pinged=true, has room)");
                return AddNodeResult.Added;
            }
            else if (!entry.Pinged && bucket.LiveCount < bucketLimit)
            {
                System.Diagnostics.Debug.WriteLine($"[RT_IMPL] Bucket has room but Pinged=false, will add to replacement instead");
            }

            // Bucket is full - try to replace a bad node or split
            bool isLastBucket = bucketIndex == _buckets.Count - 1;
            bool canSplit = isLastBucket && _buckets.Count < NodeId.BitLength - 1;

            // Try to split first (libtorrent splits regardless of confirmed status;
            // confirmed/pinged only affects placement within the split buckets)
            if (canSplit && !AllInSameBucket(bucket, entry.Id, bucketIndex))
            {
                System.Diagnostics.Debug.WriteLine($"[RT_IMPL] Requesting bucket split");
                return AddNodeResult.NeedBucketSplit;
            }

            if (entry.IsConfirmed)
            {
                // Try to replace a failed node
                var badNode = bucket.FindWorstNode();
                if (badNode != null && badNode.FailCount > 0)
                {
                    bucket.RemoveLive(badNode);
                    if (badNode.Address != null) _ips.Remove(badNode.Address);
                    NodeRemoved?.Invoke(badNode);

                    bucket.AddLive(entry);
                    if (entry.Address != null) _ips.Add(entry.Address);
                    NodeAdded?.Invoke(entry);
                    System.Diagnostics.Debug.WriteLine($"[RT_IMPL] Replaced bad node {badNode.Id.ToShortHex()} with confirmed node");
                    return AddNodeResult.Added;
                }

                // RTT-based replacement: if incoming node has known RTT,
                // replace the worst node if it has significantly higher RTT
                // (libtorrent prefers responsive nodes)
                if (entry.RttMs > 0 && badNode != null)
                {
                    int worstRtt = badNode.RttMs == 0 ? int.MaxValue : badNode.RttMs;
                    int entryRtt = entry.RttMs;

                    // Replace if incoming node is 2x faster, or worst has unknown RTT
                    if (worstRtt > entryRtt * 2 || badNode.RttMs == 0)
                    {
                        // Only replace unverified nodes with verified ones via RTT
                        if (!badNode.Verified && entry.Verified)
                        {
                            bucket.RemoveLive(badNode);
                            if (badNode.Address != null) _ips.Remove(badNode.Address);
                            NodeRemoved?.Invoke(badNode);

                            bucket.AddLive(entry);
                            if (entry.Address != null) _ips.Add(entry.Address);
                            NodeAdded?.Invoke(entry);
                            System.Diagnostics.Debug.WriteLine(
                                $"[RT_IMPL] RTT replacement: evicted {badNode.Id.ToShortHex()} (RTT={badNode.RttMs}ms, Verified={badNode.Verified}) for {entry.Id.ToShortHex()} (RTT={entry.RttMs}ms, Verified={entry.Verified})");
                            return AddNodeResult.Added;
                        }
                    }
                }
            }

            // Add to replacement bucket
            if (bucket.ReplacementCount < DhtConstants.BucketSize)
            {
                bucket.AddReplacement(entry);
                if (entry.Address != null) _ips.Add(entry.Address);
                System.Diagnostics.Debug.WriteLine($"[RT_IMPL] Added to REPLACEMENT bucket");
                return AddNodeResult.Added;
            }

            // Replacement bucket full, try to replace worst replacement
            var worstReplacement = bucket.FindWorstReplacement();
            if (worstReplacement != null && !worstReplacement.Pinged && entry.Pinged)
            {
                bucket.RemoveReplacement(worstReplacement);
                if (worstReplacement.Address != null) _ips.Remove(worstReplacement.Address);

                bucket.AddReplacement(entry);
                if (entry.Address != null) _ips.Add(entry.Address);
                System.Diagnostics.Debug.WriteLine($"[RT_IMPL] Replaced worst replacement node");
                return AddNodeResult.Added;
            }

            System.Diagnostics.Debug.WriteLine($"[RT_IMPL] All options exhausted, failed to add");
            return AddNodeResult.Failed;
        }

        private bool AllInSameBucket(RoutingBucket bucket, NodeId newId, int bucketIndex)
        {
            int byteOffset = bucketIndex / 8;
            int bitOffset = bucketIndex % 8;
            byte mask = (byte)(0x80 >> bitOffset);

            bool newIdBit = (newId.Bytes[byteOffset] & mask) != 0;
            int sameCount = 0;
            int diffCount = 0;

            foreach (var node in bucket.LiveNodes)
            {
                bool nodeBit = (node.Id.Bytes[byteOffset] & mask) != 0;
                if (nodeBit == newIdBit) sameCount++;
                else diffCount++;
            }

            // All nodes (including new one) would be in the same side
            return sameCount == 0 || diffCount == 0;
        }

        private void SplitBucket()
        {
            int oldBucketIndex = _buckets.Count - 1;
            var oldBucket = _buckets[oldBucketIndex];

            // Create new bucket
            var newBucket = new RoutingBucket(DhtConstants.BucketSize);
            _buckets.Add(newBucket);

            int newBucketIndex = _buckets.Count - 1;
            int oldBucketLimit = BucketLimit(oldBucketIndex);
            int newBucketLimit = BucketLimit(newBucketIndex);

            // Move nodes that belong to the new bucket
            var nodesToMove = new List<NodeEntry>();
            foreach (var node in oldBucket.LiveNodes.ToList())
            {
                int distExp = NodeId.DistanceExp(_id, node.Id);
                if (distExp < NodeId.BitLength - 1 - oldBucketIndex)
                {
                    nodesToMove.Add(node);
                }
            }

            foreach (var node in nodesToMove)
            {
                oldBucket.RemoveLive(node);
                if (newBucket.LiveCount < newBucketLimit)
                {
                    newBucket.AddLive(node);
                }
                else
                {
                    newBucket.AddReplacement(node);
                }
            }

            // Same for replacement nodes
            var replacementsToMove = new List<NodeEntry>();
            foreach (var node in oldBucket.ReplacementNodes.ToList())
            {
                int distExp = NodeId.DistanceExp(_id, node.Id);
                if (distExp < NodeId.BitLength - 1 - oldBucketIndex)
                {
                    replacementsToMove.Add(node);
                }
            }

            foreach (var node in replacementsToMove)
            {
                oldBucket.RemoveReplacement(node);
                if (node.Pinged && newBucket.LiveCount < newBucketLimit)
                {
                    newBucket.AddLive(node);
                }
                else
                {
                    newBucket.AddReplacement(node);
                }
            }

            // If old bucket is over limit, move excess to replacements
            while (oldBucket.LiveCount > oldBucketLimit)
            {
                var worst = oldBucket.FindWorstNode();
                if (worst == null) break;

                oldBucket.RemoveLive(worst);
                oldBucket.AddReplacement(worst);
            }

            // Remove empty new bucket if nothing was moved
            if (newBucket.LiveCount == 0 && newBucket.ReplacementCount == 0)
            {
                _buckets.RemoveAt(_buckets.Count - 1);
            }
        }

        private void TryPromoteFromReplacement(RoutingBucket bucket, int bucketLimit)
        {
            while (bucket.LiveCount < bucketLimit && bucket.ReplacementCount > 0)
            {
                var best = bucket.ReplacementNodes
                    .Where(n => n.Pinged)
                    .OrderBy(n => n)
                    .FirstOrDefault();

                if (best == null) break;

                bucket.RemoveReplacement(best);
                bucket.AddLive(best);
            }
        }

        /// <summary>
        /// Called when a node fails to respond to a query.
        /// </summary>
        public void NodeFailed(NodeId id, EndPoint endpoint)
        {
            lock (_lock)
            {
                int bucketIndex = FindBucketIndex(id);
                if (bucketIndex >= _buckets.Count) return;

                var bucket = _buckets[bucketIndex];
                var node = bucket.FindById(id);

                if (node != null && node.NetworkEndPoint.Equals(endpoint))
                {
                    node.OnQueryTimeout();

                    if (node.IsBad(_dhtMonitor.CurrentValue.MaxFailCount))
                    {
                        bucket.RemoveLive(node);
                        if (node.Address != null) _ips.Remove(node.Address);
                        NodeRemoved?.Invoke(node);

                        TryPromoteFromReplacement(bucket, BucketLimit(bucketIndex));
                    }
                }
                else
                {
                    // Check replacement bucket
                    var replacement = bucket.FindReplacementById(id);
                    if (replacement != null && replacement.NetworkEndPoint.Equals(endpoint))
                    {
                        replacement.OnQueryTimeout();
                    }
                }
            }
        }

        /// <summary>
        /// Called when a node successfully responds to a query.
        /// This marks the node as "pinged" (verified) and promotes it to live bucket.
        /// </summary>
        public void NodeSeen(NodeId id, IPEndPoint endpoint, int rttMs)
        {
            var entry = new NodeEntry(id, endpoint, rttMs, true);
            AddNode(entry);
        }

        /// <summary>
        /// Called when we hear about a node (e.g., from get_peers response "nodes" field).
        /// This is like libtorrent's heard_about() - adds node to routing table without
        /// requiring a direct response. The node will be added to live bucket if there's room,
        /// otherwise to replacement bucket.
        /// </summary>
        /// <param name="id">The node's ID.</param>
        /// <param name="endpoint">The node's endpoint.</param>
        /// <returns>True if the node was added, false if rejected.</returns>
        public bool HeardAbout(NodeId id, IPEndPoint endpoint)
        {
            // Create entry with Pinged=false (we haven't verified it yet)
            var entry = new NodeEntry(id, endpoint, 0, false);
            return AddNodeHeardAbout(entry);
        }

        /// <summary>
        /// Special add method for heard_about nodes - allows adding unpinged nodes to live bucket
        /// if there's room, matching libtorrent's behavior.
        /// </summary>
        private bool AddNodeHeardAbout(NodeEntry entry)
        {
            if (entry == null) return false;
            if (entry.Id.Equals(_id)) return false;

            lock (_lock)
            {
                // If this was a router node, promote it
                if (_routerNodes.Contains(entry.NetworkEndPoint))
                {
                    _routerNodes.Remove(entry.NetworkEndPoint);
                }

                // Check IP restrictions
                if (entry.Address != null && _dhtMonitor.CurrentValue.RestrictRoutingIps && _ips.Contains(entry.Address))
                {
                    var existing = FindNodeByEndpoint(entry.NetworkEndPoint);
                    if (existing != null)
                    {
                        if (existing.Id.Equals(entry.Id))
                        {
                            // Same node, already have it
                            return true;
                        }
                        // Different ID for same IP - reject
                        return false;
                    }
                    // Different port same IP - reject
                    return false;
                }

                int bucketIndex = FindBucketIndex(entry.Id);
                var bucket = _buckets[bucketIndex];
                int bucketLimit = BucketLimit(bucketIndex);

                // Check if node already exists
                var existing2 = bucket.FindById(entry.Id);
                if (existing2 != null) return true; // Already have it

                var replacement = bucket.FindReplacementById(entry.Id);
                if (replacement != null) return true; // Already have it

                // Key difference from AddNode: add to live bucket even if unpinged,
                // as long as there's room (libtorrent behavior)
                if (bucket.LiveCount < bucketLimit)
                {
                    bucket.AddLive(entry);
                    if (entry.Address != null) _ips.Add(entry.Address);
                    NodeAdded?.Invoke(entry);
                    return true;
                }

                // Bucket full - try to split (same logic as AddNodeImpl)
                bool isLastBucket = bucketIndex == _buckets.Count - 1;
                bool canSplit = isLastBucket && _buckets.Count < NodeId.BitLength - 1;

                if (canSplit && !AllInSameBucket(bucket, entry.Id, bucketIndex))
                {
                    if (_buckets.Count < 50) // Sanity limit
                    {
                        SplitBucket();
                        // After split, retry adding to the correct bucket
                        bucketIndex = FindBucketIndex(entry.Id);
                        bucket = _buckets[bucketIndex];
                        bucketLimit = BucketLimit(bucketIndex);

                        if (bucket.LiveCount < bucketLimit)
                        {
                            bucket.AddLive(entry);
                            if (entry.Address != null) _ips.Add(entry.Address);
                            NodeAdded?.Invoke(entry);
                            return true;
                        }
                    }
                }

                // Bucket full - add to replacement if there's room
                if (bucket.ReplacementCount < DhtConstants.BucketSize)
                {
                    bucket.AddReplacement(entry);
                    if (entry.Address != null) _ips.Add(entry.Address);
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Gets the index of a bucket that needs refreshing (hasn't been active recently
        /// or has room for more nodes). Used for proactive DHT growth.
        /// </summary>
        /// <returns>Bucket index to refresh, or -1 if none need refresh.</returns>
        public int GetBucketNeedingRefresh()
        {
            var refreshTime = TimeSpan.FromMilliseconds(DhtConstants.BucketRefreshIntervalMs);
            var now = DateTime.UtcNow;

            lock (_lock)
            {
                // Prefer buckets that aren't full and haven't been changed recently
                for (int i = _buckets.Count - 1; i >= 0; i--)
                {
                    var bucket = _buckets[i];
                    int bucketLimit = BucketLimit(i);

                    // Bucket has room and hasn't been active
                    if (bucket.LiveCount < bucketLimit && (now - bucket.LastChanged) > refreshTime)
                    {
                        return i;
                    }
                }

                // Then check for buckets that just need refreshing
                for (int i = _buckets.Count - 1; i >= 0; i--)
                {
                    var bucket = _buckets[i];
                    if ((now - bucket.LastChanged) > refreshTime)
                    {
                        return i;
                    }
                }

                return -1;
            }
        }

        /// <summary>
        /// Generates a random node ID that would fall into the specified bucket.
        /// Used for bucket refresh operations.
        /// </summary>
        public NodeId GenerateRandomIdForBucket(int bucketIndex)
        {
            // Generate an ID that would fall into the target bucket
            // The bucket index corresponds to the number of leading bits that differ from our ID
            return NodeId.GenerateWithPrefix(_id, bucketIndex);
        }

        /// <summary>
        /// Checks if a bucket has room for more nodes.
        /// </summary>
        public bool BucketHasRoom(int bucketIndex)
        {
            lock (_lock)
            {
                if (bucketIndex < 0 || bucketIndex >= _buckets.Count)
                    return false;

                var bucket = _buckets[bucketIndex];
                return bucket.LiveCount < BucketLimit(bucketIndex);
            }
        }

        /// <summary>
        /// Finds the K nodes closest to the given target ID.
        /// </summary>
        /// <param name="target">Target node ID to find closest nodes for</param>
        /// <param name="count">Number of nodes to return (default: bucket size)</param>
        /// <param name="includeQuestionable">Include nodes that haven't been confirmed recently</param>
        /// <param name="includeReplacements">Include nodes from replacement buckets (for bootstrapping)</param>
        public List<NodeEntry> FindClosestNodes(NodeId target, int count = 0, bool includeQuestionable = false, bool includeReplacements = false)
        {
            if (count == 0) count = DhtConstants.BucketSize;

            lock (_lock)
            {
                var result = new List<NodeEntry>();
                int targetBucket = FindBucketIndex(target);

                System.Diagnostics.Debug.WriteLine($"[RT_FIND] FindClosestNodes for {target.ToShortHex()}, count={count}, includeQuestionable={includeQuestionable}, includeReplacements={includeReplacements}");
                System.Diagnostics.Debug.WriteLine($"[RT_FIND] Target bucket: {targetBucket}, total buckets: {_buckets.Count}");

                // Count total live and replacement nodes
                int totalLive = _buckets.Sum(b => b.LiveCount);
                int totalReplacement = _buckets.Sum(b => b.ReplacementCount);
                System.Diagnostics.Debug.WriteLine($"[RT_FIND] Total nodes: {totalLive} live, {totalReplacement} replacement");

                // Gather nodes from nearby buckets - first live nodes
                for (int i = targetBucket; i < _buckets.Count && result.Count < count * 2; i++)
                {
                    var bucket = _buckets[i];
                    System.Diagnostics.Debug.WriteLine($"[RT_FIND] Bucket {i}: {bucket.LiveCount} live, {bucket.ReplacementCount} replacement");
                    foreach (var node in bucket.LiveNodes)
                    {
                        if (includeQuestionable || node.IsConfirmed)
                        {
                            result.Add(node);
                            System.Diagnostics.Debug.WriteLine($"[RT_FIND] + Live {node.Id.ToShortHex()} @ {node.NetworkEndPoint} (Pinged={node.Pinged}, Confirmed={node.IsConfirmed})");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[RT_FIND] - Skipped {node.Id.ToShortHex()} (Pinged={node.Pinged}, Confirmed={node.IsConfirmed})");
                        }
                    }
                }

                for (int i = targetBucket - 1; i >= 0 && result.Count < count * 2; i--)
                {
                    var bucket = _buckets[i];
                    System.Diagnostics.Debug.WriteLine($"[RT_FIND] Bucket {i}: {bucket.LiveCount} live, {bucket.ReplacementCount} replacement");
                    foreach (var node in bucket.LiveNodes)
                    {
                        if (includeQuestionable || node.IsConfirmed)
                        {
                            result.Add(node);
                            System.Diagnostics.Debug.WriteLine($"[RT_FIND] + Live {node.Id.ToShortHex()} @ {node.NetworkEndPoint}");
                        }
                    }
                }

                // If we don't have enough nodes and includeReplacements is true, add from replacement buckets
                // This is critical for bootstrapping when live buckets are sparse
                if (includeReplacements && result.Count < count)
                {
                    System.Diagnostics.Debug.WriteLine($"[RT_FIND] Not enough live nodes ({result.Count}), adding from replacement buckets");
                    var existingIds = new HashSet<NodeId>(result.Select(n => n.Id));

                    for (int i = targetBucket; i < _buckets.Count && result.Count < count * 2; i++)
                    {
                        foreach (var node in _buckets[i].ReplacementNodes)
                        {
                            if (!existingIds.Contains(node.Id))
                            {
                                result.Add(node);
                                existingIds.Add(node.Id);
                                System.Diagnostics.Debug.WriteLine($"[RT_FIND] + Replacement {node.Id.ToShortHex()} @ {node.NetworkEndPoint}");
                            }
                        }
                    }

                    for (int i = targetBucket - 1; i >= 0 && result.Count < count * 2; i--)
                    {
                        foreach (var node in _buckets[i].ReplacementNodes)
                        {
                            if (!existingIds.Contains(node.Id))
                            {
                                result.Add(node);
                                existingIds.Add(node.Id);
                                System.Diagnostics.Debug.WriteLine($"[RT_FIND] + Replacement {node.Id.ToShortHex()} @ {node.NetworkEndPoint}");
                            }
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[RT_FIND] Gathered {result.Count} nodes before sorting");

                // Sort by distance to target and take closest
                var sorted = result
                    .OrderBy(n => NodeId.Distance(n.Id, target))
                    .Take(count)
                    .ToList();

                System.Diagnostics.Debug.WriteLine($"[RT_FIND] Returning {sorted.Count} closest nodes");
                return sorted;
            }
        }

        /// <summary>
        /// Gets the next node that needs to be refreshed (pinged).
        /// Returns null if no nodes need refreshing yet.
        /// </summary>
        public NodeEntry GetNextRefreshCandidate()
        {
            var questionableTime = TimeSpan.FromMilliseconds(DhtConstants.NodeQuestionableTimeMs);
            var now = DateTime.UtcNow;

            // Minimum time between queries to the same node (60 seconds)
            // This prevents hammering a single node when routing table is small
            var minQueryInterval = TimeSpan.FromSeconds(60);

            lock (_lock)
            {
                NodeEntry candidate = null;

                // Prefer nodes closer to our ID (reverse iteration)
                for (int i = _buckets.Count - 1; i >= 0; i--)
                {
                    var bucket = _buckets[i];

                    // Check for nodes that have never been queried
                    foreach (var node in bucket.LiveNodes)
                    {
                        if (node.Id.Equals(_id)) continue;

                        // Never queried - good candidate
                        if (node.LastQueried == DateTime.MinValue)
                        {
                            node.OnQuerySent();
                            return node;
                        }

                        // Skip if queried too recently
                        if ((now - node.LastQueried) < minQueryInterval)
                        {
                            continue;
                        }

                        if (candidate == null || node.LastQueried < candidate.LastQueried)
                        {
                            candidate = node;
                        }
                    }

                    // Also check unpinged replacements in non-full buckets
                    if (bucket.LiveCount < BucketLimit(i))
                    {
                        foreach (var node in bucket.ReplacementNodes)
                        {
                            if (!node.Pinged && node.LastQueried == DateTime.MinValue)
                            {
                                node.OnQuerySent();
                                return node;
                            }
                        }
                    }
                }

                // Only return candidate if it hasn't been queried too recently
                if (candidate != null && (now - candidate.LastQueried) >= minQueryInterval)
                {
                    candidate.OnQuerySent();
                    return candidate;
                }

                return null;
            }
        }

        /// <summary>
        /// Gets statistics about the routing table.
        /// </summary>
        public RoutingTableStats GetStats()
        {
            lock (_lock)
            {
                int liveNodes = 0;
                int replacements = 0;
                int confirmed = 0;

                foreach (var bucket in _buckets)
                {
                    liveNodes += bucket.LiveCount;
                    replacements += bucket.ReplacementCount;
                    confirmed += bucket.LiveNodes.Count(n => n.IsConfirmed);
                }

                return new RoutingTableStats
                {
                    NumBuckets = _buckets.Count,
                    LiveNodes = liveNodes,
                    ReplacementNodes = replacements,
                    ConfirmedNodes = confirmed,
                    RouterNodes = _routerNodes.Count
                };
            }
        }

        /// <summary>
        /// Gets all nodes in the routing table for persistence.
        /// </summary>
        public List<NodeEntry> GetAllNodes()
        {
            lock (_lock)
            {
                var result = new List<NodeEntry>();
                foreach (var bucket in _buckets)
                {
                    result.AddRange(bucket.LiveNodes);
                    result.AddRange(bucket.ReplacementNodes);
                }
                return result;
            }
        }

        /// <summary>
        /// Iterates over all live nodes.
        /// </summary>
        public void ForEachNode(Action<NodeEntry> action)
        {
            lock (_lock)
            {
                foreach (var bucket in _buckets)
                {
                    foreach (var node in bucket.LiveNodes)
                    {
                        action(node);
                    }
                }
            }
        }

        private NodeEntry FindNodeByEndpoint(EndPoint endpoint)
        {
            foreach (var bucket in _buckets)
            {
                var node = bucket.LiveNodes.FirstOrDefault(n => n.NetworkEndPoint.Equals(endpoint));
                if (node != null) return node;

                node = bucket.ReplacementNodes.FirstOrDefault(n => n.NetworkEndPoint.Equals(endpoint));
                if (node != null) return node;
            }
            return null;
        }

        /// <summary>
        /// Updates our node ID (expensive operation).
        /// </summary>
        public void UpdateNodeId(NodeId newId)
        {
            lock (_lock)
            {
                var allNodes = GetAllNodes();

                _id = newId;
                _buckets.Clear();
                _ips.Clear();
                _buckets.Add(new RoutingBucket(DhtConstants.BucketSize));

                foreach (var node in allNodes)
                {
                    AddNode(node);
                }
            }
        }
    }

    /// <summary>
    /// Statistics about the routing table.
    /// </summary>
    public struct RoutingTableStats
    {
        public int NumBuckets { get; set; }
        public int LiveNodes { get; set; }
        public int ReplacementNodes { get; set; }
        public int ConfirmedNodes { get; set; }
        public int RouterNodes { get; set; }
    }

    /// <summary>
    /// A single bucket in the routing table.
    /// </summary>
    internal class RoutingBucket
    {
        private readonly List<NodeEntry> _liveNodes;
        private readonly List<NodeEntry> _replacements;
        private readonly int _maxSize;

        public DateTime LastChanged { get; private set; }
        public int LiveCount => _liveNodes.Count;
        public int ReplacementCount => _replacements.Count;
        public IReadOnlyList<NodeEntry> LiveNodes => _liveNodes;
        public IReadOnlyList<NodeEntry> ReplacementNodes => _replacements;

        public RoutingBucket(int maxSize)
        {
            _maxSize = maxSize;
            _liveNodes = new List<NodeEntry>(maxSize);
            _replacements = new List<NodeEntry>(maxSize);
            LastChanged = DateTime.UtcNow;
        }

        public void AddLive(NodeEntry entry)
        {
            _liveNodes.Add(entry);
            LastChanged = DateTime.UtcNow;
        }

        public void RemoveLive(NodeEntry entry)
        {
            _liveNodes.Remove(entry);
            LastChanged = DateTime.UtcNow;
        }

        public void AddReplacement(NodeEntry entry)
        {
            _replacements.Add(entry);
        }

        public void RemoveReplacement(NodeEntry entry)
        {
            _replacements.Remove(entry);
        }

        public NodeEntry FindById(NodeId id)
        {
            return _liveNodes.FirstOrDefault(n => n.Id.Equals(id));
        }

        public NodeEntry FindReplacementById(NodeId id)
        {
            return _replacements.FirstOrDefault(n => n.Id.Equals(id));
        }

        public NodeEntry FindWorstNode()
        {
            return _liveNodes.OrderByDescending(n => n).FirstOrDefault();
        }

        public NodeEntry FindWorstReplacement()
        {
            return _replacements.OrderByDescending(n => n).FirstOrDefault();
        }
    }
}
