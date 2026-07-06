
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;

namespace vTorrent.Core.DHT
{
    /// <summary>
    /// DHT KRPC message types per BEP 5.
    /// </summary>
    public enum DhtMessageType
    {
        Query,
        Response,
        Error
    }

    /// <summary>
    /// DHT query types per BEP 5.
    /// </summary>
    public enum DhtQueryType
    {
        Ping,
        FindNode,
        GetPeers,
        AnnouncePeer,
        SampleInfohashes,
        Unknown
    }

    /// <summary>
    /// DHT error codes per BEP 5.
    /// </summary>
    public enum DhtErrorCode
    {
        GenericError = 201,
        ServerError = 202,
        ProtocolError = 203,
        MethodUnknown = 204
    }

    /// <summary>
    /// Represents a KRPC message for DHT communication.
    /// All messages are bencoded dictionaries sent over UDP.
    /// </summary>
    public class DhtMessage
    {
        private static readonly BencodeParser Parser = new();

        /// <summary>
        /// Transaction ID for matching requests and responses (2 bytes typically).
        /// </summary>
        public byte[] TransactionId { get; set; }

        /// <summary>
        /// Message type: query, response, or error.
        /// </summary>
        public DhtMessageType MessageType { get; set; }

        /// <summary>
        /// For queries: the query type (ping, find_node, get_peers, announce_peer).
        /// </summary>
        public DhtQueryType QueryType { get; set; }

        /// <summary>
        /// Client version string (optional).
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// The source endpoint of this message.
        /// </summary>
        public IPEndPoint SourceEndpoint { get; set; }

        /// <summary>
        /// For queries/responses: the sending node's ID.
        /// </summary>
        public NodeId NodeId { get; set; }

        /// <summary>
        /// For find_node: the target node ID to find.
        /// </summary>
        public NodeId Target { get; set; }

        /// <summary>
        /// For get_peers/announce_peer: the info_hash.
        /// </summary>
        public byte[] InfoHash { get; set; }

        /// <summary>
        /// For find_node/get_peers responses: compact node info.
        /// Each node is 26 bytes: 20-byte ID + 4-byte IP + 2-byte port.
        /// </summary>
        public byte[] Nodes { get; set; }

        /// <summary>
        /// For get_peers responses: compact peer info.
        /// Each peer is 6 bytes: 4-byte IP + 2-byte port.
        /// </summary>
        public List<byte[]> Values { get; set; }

        /// <summary>
        /// For get_peers responses: write token for announce_peer.
        /// </summary>
        public byte[] Token { get; set; }

        /// <summary>
        /// For announce_peer: the port we're listening on.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// For announce_peer: if true, use source port instead of Port field.
        /// </summary>
        public bool ImpliedPort { get; set; }

        /// <summary>
        /// BEP 43: Read-only node flag (top-level "ro" field).
        /// </summary>
        public bool ReadOnly { get; set; }

        /// <summary>
        /// BEP 33: Request bloom filters in get_peers query.
        /// </summary>
        public bool Scrape { get; set; }

        /// <summary>
        /// BEP 33: Exclude seeds from get_peers response.
        /// </summary>
        public bool NoSeed { get; set; }

        /// <summary>
        /// BEP 33: Announcing peer is a seed.
        /// </summary>
        public bool IsSeed { get; set; }

        /// <summary>
        /// BEP 33: Bloom filter of seeds in get_peers response (256 bytes).
        /// </summary>
        public byte[] BFsd { get; set; }

        /// <summary>
        /// BEP 33: Bloom filter of peers in get_peers response (256 bytes).
        /// </summary>
        public byte[] BFpe { get; set; }

        /// <summary>
        /// BEP 51: Concatenated 20-byte infohash samples.
        /// </summary>
        public byte[] Samples { get; set; }

        /// <summary>
        /// BEP 51: Total number of infohashes stored on the responding node.
        /// </summary>
        public int SampleNum { get; set; }

        /// <summary>
        /// BEP 51: Recommended re-query interval in seconds.
        /// </summary>
        public int SampleInterval { get; set; }

        /// <summary>
        /// For error responses: error code.
        /// </summary>
        public DhtErrorCode ErrorCode { get; set; }

        /// <summary>
        /// For error responses: error message.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// The raw parsed bencode dictionary.
        /// </summary>
        public BDictionary RawMessage { get; private set; }

        /// <summary>
        /// Parses a DHT message from raw bytes.
        /// </summary>
        public static DhtMessage Parse(byte[] data, IPEndPoint sourceEndpoint)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Empty data", nameof(data));

            var obj = Parser.Parse(data, out _);
            if (obj is not BDictionary dict)
                throw new InvalidOperationException("DHT message must be a dictionary");

            var message = new DhtMessage
            {
                RawMessage = dict,
                SourceEndpoint = sourceEndpoint
            };

            // Transaction ID (required)
            if (dict.TryGetValue("t", out var tObj) && tObj is BString tStr)
            {
                message.TransactionId = tStr.Value.ToArray();
            }
            else
            {
                throw new InvalidOperationException("Missing transaction ID");
            }

            // Message type (required)
            if (dict.TryGetValue("y", out var yObj) && yObj is BString yStr)
            {
                string msgType = yStr.ToString();
                message.MessageType = msgType switch
                {
                    "q" => DhtMessageType.Query,
                    "r" => DhtMessageType.Response,
                    "e" => DhtMessageType.Error,
                    _ => throw new InvalidOperationException($"Unknown message type: {msgType}")
                };
            }
            else
            {
                throw new InvalidOperationException("Missing message type");
            }

            // Version (optional)
            if (dict.TryGetValue("v", out var vObj) && vObj is BString vStr)
            {
                message.Version = vStr.ToString();
            }

            // BEP 43: Read-only flag (top-level)
            if (dict.TryGetValue("ro", out var roObj) && roObj is BNumber roNum)
                message.ReadOnly = roNum.Value == 1;

            // Parse based on message type
            switch (message.MessageType)
            {
                case DhtMessageType.Query:
                    ParseQuery(message, dict);
                    break;
                case DhtMessageType.Response:
                    ParseResponse(message, dict);
                    break;
                case DhtMessageType.Error:
                    ParseError(message, dict);
                    break;
            }

            return message;
        }

        private static void ParseQuery(DhtMessage message, BDictionary dict)
        {
            // Query type
            if (dict.TryGetValue("q", out var qObj) && qObj is BString qStr)
            {
                message.QueryType = qStr.ToString() switch
                {
                    "ping" => DhtQueryType.Ping,
                    "find_node" => DhtQueryType.FindNode,
                    "get_peers" => DhtQueryType.GetPeers,
                    "announce_peer" => DhtQueryType.AnnouncePeer,
                    "sample_infohashes" => DhtQueryType.SampleInfohashes,
                    _ => DhtQueryType.Unknown
                };
            }

            // Arguments
            if (dict.TryGetValue("a", out var aObj) && aObj is BDictionary args)
            {
                // Node ID (required in all queries)
                if (args.TryGetValue("id", out var idObj) && idObj is BString idStr)
                {
                    message.NodeId = new NodeId(idStr.Value.Span);
                }

                // Target (for find_node)
                if (args.TryGetValue("target", out var targetObj) && targetObj is BString targetStr)
                {
                    message.Target = new NodeId(targetStr.Value.Span);
                }

                // Info hash (for get_peers/announce_peer)
                if (args.TryGetValue("info_hash", out var hashObj) && hashObj is BString hashStr)
                {
                    message.InfoHash = hashStr.Value.ToArray();
                }

                // Port (for announce_peer)
                if (args.TryGetValue("port", out var portObj) && portObj is BNumber portNum)
                {
                    message.Port = (int)portNum.Value;
                }

                // Token (for announce_peer)
                if (args.TryGetValue("token", out var tokenObj) && tokenObj is BString tokenStr)
                {
                    message.Token = tokenStr.Value.ToArray();
                }

                // Implied port (for announce_peer)
                if (args.TryGetValue("implied_port", out var impliedObj) && impliedObj is BNumber impliedNum)
                {
                    message.ImpliedPort = impliedNum.Value != 0;
                }

                // BEP 33: scrape flag (get_peers)
                if (args.TryGetValue("scrape", out var scrapeObj) && scrapeObj is BNumber scrapeNum)
                    message.Scrape = scrapeNum.Value == 1;

                // BEP 33: noseed flag (get_peers)
                if (args.TryGetValue("noseed", out var noseedObj) && noseedObj is BNumber noseedNum)
                    message.NoSeed = noseedNum.Value == 1;

                // BEP 33: seed flag (announce_peer)
                if (args.TryGetValue("seed", out var seedObj) && seedObj is BNumber seedNum)
                    message.IsSeed = seedNum.Value == 1;
            }
        }

        private static void ParseResponse(DhtMessage message, BDictionary dict)
        {
            if (dict.TryGetValue("r", out var rObj) && rObj is BDictionary response)
            {
                // Log all keys in response for debugging
                var keys = string.Join(", ", response.Keys);
                System.Diagnostics.Debug.WriteLine($"[DHT_PARSE] Response dictionary keys: {keys}");

                // Node ID
                if (response.TryGetValue("id", out var idObj) && idObj is BString idStr)
                {
                    message.NodeId = new NodeId(idStr.Value.Span);
                    System.Diagnostics.Debug.WriteLine($"[DHT_PARSE] NodeId: {message.NodeId.ToShortHex()}");
                }

                // Nodes (compact node info)
                if (response.TryGetValue("nodes", out var nodesObj) && nodesObj is BString nodesStr)
                {
                    message.Nodes = nodesStr.Value.ToArray();
                    int nodeCount = message.Nodes.Length / 26;
                    System.Diagnostics.Debug.WriteLine($"[DHT_PARSE] Nodes: {message.Nodes.Length} bytes ({nodeCount} nodes)");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[DHT_PARSE] No 'nodes' field in response");
                }

                // Values (peers) - handle both list format and single binary string format
                if (response.TryGetValue("values", out var valuesObj))
                {
                    message.Values = new List<byte[]>();

                    if (valuesObj is BList valuesList)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DHT_PARSE] Values: BList with {valuesList.Count} items");
                        foreach (var item in valuesList)
                        {
                            if (item is BString peerStr)
                            {
                                var peerBytes = peerStr.Value.ToArray();
                                message.Values.Add(peerBytes);
                                if (peerBytes.Length == 6)
                                {
                                    var ip = new IPAddress(peerBytes.AsSpan(0, 4));
                                    int port = (peerBytes[4] << 8) | peerBytes[5];
                                    System.Diagnostics.Debug.WriteLine($"[DHT_PARSE] Peer: {ip}:{port}");
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"[DHT_PARSE] Peer entry with unexpected length: {peerBytes.Length} bytes");
                                }
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"[DHT_PARSE] Values list item is not BString: {item.GetType().Name}");
                            }
                        }
                    }
                    else if (valuesObj is BString valuesBStr)
                    {
                        // Some implementations return peers as a single binary string (mainline format)
                        var data = valuesBStr.Value.ToArray();
                        System.Diagnostics.Debug.WriteLine($"[DHT_PARSE] Values: BString with {data.Length} bytes (mainline format)");
                        for (int i = 0; i + 6 <= data.Length; i += 6)
                        {
                            var peerBytes = new byte[6];
                            Array.Copy(data, i, peerBytes, 0, 6);
                            message.Values.Add(peerBytes);
                            var ip = new IPAddress(peerBytes.AsSpan(0, 4));
                            int port = (peerBytes[4] << 8) | peerBytes[5];
                            System.Diagnostics.Debug.WriteLine($"[DHT_PARSE] Peer (mainline): {ip}:{port}");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[DHT_PARSE] Values has unexpected type: {valuesObj.GetType().Name}");
                    }

                    System.Diagnostics.Debug.WriteLine($"[DHT_PARSE] Total peers parsed: {message.Values.Count}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[DHT_PARSE] No 'values' field in response");
                }

                // Token
                if (response.TryGetValue("token", out var tokenObj) && tokenObj is BString tokenStr)
                {
                    message.Token = tokenStr.Value.ToArray();
                    System.Diagnostics.Debug.WriteLine($"[DHT_PARSE] Token: {message.Token.Length} bytes");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[DHT_PARSE] No 'token' field in response");
                }

                // BEP 33: Bloom filter of seeds
                if (response.TryGetValue("BFsd", out var bfsdObj) && bfsdObj is BString bfsdStr && bfsdStr.Value.Length == 256)
                    message.BFsd = bfsdStr.Value.ToArray();

                // BEP 33: Bloom filter of peers
                if (response.TryGetValue("BFpe", out var bfpeObj) && bfpeObj is BString bfpeStr && bfpeStr.Value.Length == 256)
                    message.BFpe = bfpeStr.Value.ToArray();

                // BEP 51: sample_infohashes response fields
                if (response.TryGetValue("samples", out var samplesObj) && samplesObj is BString samplesStr)
                    message.Samples = samplesStr.Value.ToArray();

                if (response.TryGetValue("num", out var numObj) && numObj is BNumber numNum)
                    message.SampleNum = (int)numNum.Value;

                if (response.TryGetValue("interval", out var intervalObj) && intervalObj is BNumber intervalNum)
                    message.SampleInterval = (int)intervalNum.Value;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[DHT_PARSE] No 'r' dictionary in response message");
            }
        }

        private static void ParseError(DhtMessage message, BDictionary dict)
        {
            if (dict.TryGetValue("e", out var eObj) && eObj is BList errorList && errorList.Count >= 2)
            {
                if (errorList[0] is BNumber errorCode)
                {
                    message.ErrorCode = (DhtErrorCode)errorCode.Value;
                }

                if (errorList[1] is BString errorMsg)
                {
                    message.ErrorMessage = errorMsg.ToString();
                }
            }
        }

        /// <summary>
        /// Encodes this message to bytes for transmission.
        /// </summary>
        public byte[] Encode()
        {
            var dict = new BDictionary();

            // Transaction ID
            dict.AddBytes("t", TransactionId);

            // Message type
            string typeStr = MessageType switch
            {
                DhtMessageType.Query => "q",
                DhtMessageType.Response => "r",
                DhtMessageType.Error => "e",
                _ => throw new InvalidOperationException()
            };
            dict.AddString("y", typeStr);

            // Version
            if (!string.IsNullOrEmpty(Version))
            {
                dict.AddString("v", Version);
            }

            switch (MessageType)
            {
                case DhtMessageType.Query:
                    EncodeQuery(dict);
                    break;
                case DhtMessageType.Response:
                    EncodeResponse(dict);
                    break;
                case DhtMessageType.Error:
                    EncodeError(dict);
                    break;
            }

            using var ms = new MemoryStream();
            dict.EncodeTo(ms);
            return ms.ToArray();
        }

        private void EncodeQuery(BDictionary dict)
        {
            string queryStr = QueryType switch
            {
                DhtQueryType.Ping => "ping",
                DhtQueryType.FindNode => "find_node",
                DhtQueryType.GetPeers => "get_peers",
                DhtQueryType.AnnouncePeer => "announce_peer",
                DhtQueryType.SampleInfohashes => "sample_infohashes",
                _ => throw new InvalidOperationException()
            };
            dict.AddString("q", queryStr);

            // BEP 43: Read-only flag (top-level)
            if (ReadOnly)
                dict.AddNumber("ro", 1);

            var args = new BDictionary();
            args.AddBytes("id", NodeId.Bytes.ToArray());

            switch (QueryType)
            {
                case DhtQueryType.FindNode:
                    args.AddBytes("target", Target.Bytes.ToArray());
                    break;

                case DhtQueryType.GetPeers:
                    args.AddBytes("info_hash", InfoHash);
                    if (Scrape) args.AddNumber("scrape", 1);
                    if (NoSeed) args.AddNumber("noseed", 1);
                    break;

                case DhtQueryType.AnnouncePeer:
                    args.AddBytes("info_hash", InfoHash);
                    args.AddNumber("port", Port);
                    args.AddBytes("token", Token);
                    if (ImpliedPort)
                    {
                        args.AddNumber("implied_port", 1);
                    }
                    if (IsSeed) args.AddNumber("seed", 1);
                    break;

                case DhtQueryType.SampleInfohashes:
                    args.AddBytes("target", Target.Bytes.ToArray());
                    break;
            }

            dict.Add("a", args);
        }

        private void EncodeResponse(BDictionary dict)
        {
            var response = new BDictionary();
            response.AddBytes("id", NodeId.Bytes.ToArray());

            if (Nodes != null && Nodes.Length > 0)
            {
                response.AddBytes("nodes", Nodes);
            }

            if (Values != null && Values.Count > 0)
            {
                var valuesList = new BList();
                foreach (var peer in Values)
                {
                    valuesList.Add(new BString(peer));
                }
                response.Add("values", valuesList);
            }

            if (Token != null && Token.Length > 0)
            {
                response.AddBytes("token", Token);
            }

            // BEP 33: Bloom filters
            if (BFsd != null && BFsd.Length == 256)
                response.AddBytes("BFsd", BFsd);
            if (BFpe != null && BFpe.Length == 256)
                response.AddBytes("BFpe", BFpe);

            // BEP 51: sample_infohashes fields — always include all three together
            // Per BEP 51, samples/num/interval must always be present in sample_infohashes responses
            if (Samples != null)
            {
                response.AddBytes("samples", Samples);
                response.AddNumber("num", SampleNum);
                response.AddNumber("interval", SampleInterval);
            }

            dict.Add("r", response);
        }

        private void EncodeError(BDictionary dict)
        {
            var errorList = new BList
            {
                new BNumber((int)ErrorCode),
                new BString(ErrorMessage ?? "Unknown error")
            };
            dict.Add("e", errorList);
        }

        // Factory methods for creating messages

        /// <summary>
        /// Creates a ping query.
        /// </summary>
        public static DhtMessage CreatePingQuery(byte[] transactionId, NodeId nodeId, bool readOnly = false)
        {
            return new DhtMessage
            {
                TransactionId = transactionId,
                MessageType = DhtMessageType.Query,
                QueryType = DhtQueryType.Ping,
                NodeId = nodeId,
                ReadOnly = readOnly,
                Version = "vT01"
            };
        }

        /// <summary>
        /// Creates a ping response.
        /// </summary>
        public static DhtMessage CreatePingResponse(byte[] transactionId, NodeId nodeId)
        {
            return new DhtMessage
            {
                TransactionId = transactionId,
                MessageType = DhtMessageType.Response,
                NodeId = nodeId,
                Version = "vT01"
            };
        }

        /// <summary>
        /// Creates a find_node query.
        /// </summary>
        public static DhtMessage CreateFindNodeQuery(byte[] transactionId, NodeId nodeId, NodeId target, bool readOnly = false)
        {
            return new DhtMessage
            {
                TransactionId = transactionId,
                MessageType = DhtMessageType.Query,
                QueryType = DhtQueryType.FindNode,
                NodeId = nodeId,
                Target = target,
                ReadOnly = readOnly,
                Version = "vT01"
            };
        }

        /// <summary>
        /// Creates a find_node response.
        /// </summary>
        public static DhtMessage CreateFindNodeResponse(byte[] transactionId, NodeId nodeId, byte[] nodes)
        {
            return new DhtMessage
            {
                TransactionId = transactionId,
                MessageType = DhtMessageType.Response,
                NodeId = nodeId,
                Nodes = nodes,
                Version = "vT01"
            };
        }

        /// <summary>
        /// Creates a get_peers query.
        /// </summary>
        public static DhtMessage CreateGetPeersQuery(byte[] transactionId, NodeId nodeId, byte[] infoHash,
            bool scrape = false, bool noSeed = false, bool readOnly = false)
        {
            return new DhtMessage
            {
                TransactionId = transactionId,
                MessageType = DhtMessageType.Query,
                QueryType = DhtQueryType.GetPeers,
                NodeId = nodeId,
                InfoHash = infoHash,
                Scrape = scrape,
                NoSeed = noSeed,
                ReadOnly = readOnly,
                Version = "vT01"
            };
        }

        /// <summary>
        /// Creates a get_peers response with peers.
        /// </summary>
        public static DhtMessage CreateGetPeersResponseWithPeers(byte[] transactionId, NodeId nodeId,
            byte[] token, List<byte[]> peers, byte[] nodes = null,
            byte[] bfsd = null, byte[] bfpe = null)
        {
            return new DhtMessage
            {
                TransactionId = transactionId,
                MessageType = DhtMessageType.Response,
                NodeId = nodeId,
                Token = token,
                Values = peers,
                Nodes = nodes,
                BFsd = bfsd,
                BFpe = bfpe,
                Version = "vT01"
            };
        }

        /// <summary>
        /// Creates a get_peers response with nodes (no peers found).
        /// </summary>
        public static DhtMessage CreateGetPeersResponseWithNodes(byte[] transactionId, NodeId nodeId,
            byte[] token, byte[] nodes)
        {
            return new DhtMessage
            {
                TransactionId = transactionId,
                MessageType = DhtMessageType.Response,
                NodeId = nodeId,
                Token = token,
                Nodes = nodes,
                Version = "vT01"
            };
        }

        /// <summary>
        /// Creates an announce_peer query.
        /// </summary>
        public static DhtMessage CreateAnnouncePeerQuery(byte[] transactionId, NodeId nodeId,
            byte[] infoHash, int port, byte[] token, bool impliedPort = false,
            bool isSeed = false, bool readOnly = false)
        {
            return new DhtMessage
            {
                TransactionId = transactionId,
                MessageType = DhtMessageType.Query,
                QueryType = DhtQueryType.AnnouncePeer,
                NodeId = nodeId,
                InfoHash = infoHash,
                Port = port,
                Token = token,
                ImpliedPort = impliedPort,
                IsSeed = isSeed,
                ReadOnly = readOnly,
                Version = "vT01"
            };
        }

        /// <summary>
        /// Creates an announce_peer response.
        /// </summary>
        public static DhtMessage CreateAnnouncePeerResponse(byte[] transactionId, NodeId nodeId)
        {
            return new DhtMessage
            {
                TransactionId = transactionId,
                MessageType = DhtMessageType.Response,
                NodeId = nodeId,
                Version = "vT01"
            };
        }

        /// <summary>
        /// Creates an error response.
        /// </summary>
        public static DhtMessage CreateErrorResponse(byte[] transactionId, DhtErrorCode code, string message)
        {
            return new DhtMessage
            {
                TransactionId = transactionId,
                MessageType = DhtMessageType.Error,
                ErrorCode = code,
                ErrorMessage = message,
                Version = "vT01"
            };
        }

        /// <summary>
        /// Creates a sample_infohashes query (BEP 51).
        /// </summary>
        public static DhtMessage CreateSampleInfohashesQuery(byte[] transactionId, NodeId nodeId, NodeId target, bool readOnly = false)
        {
            return new DhtMessage
            {
                TransactionId = transactionId,
                MessageType = DhtMessageType.Query,
                QueryType = DhtQueryType.SampleInfohashes,
                NodeId = nodeId,
                Target = target,
                ReadOnly = readOnly,
                Version = "vT01"
            };
        }

        /// <summary>
        /// Creates a sample_infohashes response (BEP 51).
        /// Samples field is always included, even when empty (per BEP 51).
        /// </summary>
        public static DhtMessage CreateSampleInfohashesResponse(
            byte[] transactionId, NodeId nodeId, byte[] nodes,
            byte[] samples, int num, int interval)
        {
            return new DhtMessage
            {
                TransactionId = transactionId,
                MessageType = DhtMessageType.Response,
                NodeId = nodeId,
                Nodes = nodes,
                Samples = samples ?? Array.Empty<byte>(),
                SampleNum = num,
                SampleInterval = interval,
                Version = "vT01"
            };
        }

        /// <summary>
        /// Parses compact node info into NodeEntry list.
        /// Each node is 26 bytes: 20-byte ID + 4-byte IP + 2-byte port.
        /// </summary>
        public static List<NodeEntry> ParseCompactNodes(byte[] data)
        {
            var result = new List<NodeEntry>();
            if (data == null || data.Length < 26) return result;

            for (int i = 0; i + 26 <= data.Length; i += 26)
            {
                try
                {
                    var entry = NodeEntry.FromCompact(data.AsSpan(i, 26));
                    result.Add(entry);
                }
                catch
                {
                    // Skip malformed entries
                }
            }

            return result;
        }

        /// <summary>
        /// Encodes a list of nodes to compact format.
        /// </summary>
        public static byte[] EncodeCompactNodes(IEnumerable<NodeEntry> nodes)
        {
            using var ms = new MemoryStream();
            foreach (var node in nodes)
            {
                ms.Write(node.ToCompact());
            }
            return ms.ToArray();
        }

        /// <summary>
        /// Parses compact peer info into endpoints.
        /// Each peer is 6 bytes: 4-byte IP + 2-byte port.
        /// </summary>
        public static List<IPEndPoint> ParseCompactPeers(List<byte[]> data)
        {
            var result = new List<IPEndPoint>();
            if (data == null) return result;

            foreach (var peer in data)
            {
                if (peer.Length == 6)
                {
                    try
                    {
                        var ip = new IPAddress(peer.AsSpan(0, 4));
                        int port = (peer[4] << 8) | peer[5];
                        result.Add(new IPEndPoint(ip, port));
                    }
                    catch
                    {
                        // Skip malformed entries
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Encodes an endpoint to compact peer format (6 bytes).
        /// </summary>
        public static byte[] EncodeCompactPeer(IPEndPoint endpoint)
        {
            var result = new byte[6];
            endpoint.Address.GetAddressBytes().CopyTo(result, 0);
            result[4] = (byte)(endpoint.Port >> 8);
            result[5] = (byte)(endpoint.Port & 0xFF);
            return result;
        }
    }
}
