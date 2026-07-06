using System;
using System.Net;

namespace vTorrent.Core.DHT
{
    /// <summary>
    /// Represents a DHT node entry in the routing table.
    /// Tracks node state per BEP 5 (good, questionable, bad) and libtorrent patterns.
    /// </summary>
    public class NodeEntry : IComparable<NodeEntry>
    {
        /// <summary>
        /// The node's 160-bit identifier.
        /// </summary>
        public NodeId Id { get; }

        /// <summary>
        /// The node's IP address. Null for non-IP transports (e.g., I2P).
        /// </summary>
        public IPAddress? Address { get; }

        /// <summary>
        /// The generic network endpoint (IPEndPoint for clearnet, I2pEndPoint for I2P).
        /// </summary>
        public EndPoint NetworkEndPoint { get; }

        /// <summary>
        /// The node's UDP port.
        /// </summary>
        public int Port { get; }

        /// <summary>
        /// When this node was first seen.
        /// </summary>
        public DateTime FirstSeen { get; }

        /// <summary>
        /// When we last received a response from this node.
        /// </summary>
        public DateTime LastSeen { get; private set; }

        /// <summary>
        /// When we last sent a query to this node.
        /// </summary>
        public DateTime LastQueried { get; private set; }

        /// <summary>
        /// Number of consecutive query timeouts.
        /// </summary>
        public int FailCount { get; private set; }

        /// <summary>
        /// Round-trip time in milliseconds (0 if unknown).
        /// </summary>
        public int RttMs { get; private set; }

        /// <summary>
        /// Whether this node has been pinged and responded.
        /// </summary>
        public bool Pinged { get; private set; }

        /// <summary>
        /// Whether this node's ID has been verified (BEP 42).
        /// </summary>
        public bool Verified { get; set; }

        /// <summary>
        /// The UDP endpoint for this node. Null for non-IP transports (e.g., I2P); use NetworkEndPoint instead.
        /// </summary>
        public IPEndPoint? EndPoint => Address != null ? new IPEndPoint(Address, Port) : null;

        /// <summary>
        /// Creates a new node entry.
        /// </summary>
        public NodeEntry(NodeId id, IPAddress address, int port, int rtt = 0, bool pinged = false)
        {
            if (address == null)
                throw new ArgumentNullException(nameof(address));
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));

            Id = id;
            Address = address;
            Port = port;
            RttMs = rtt;
            Pinged = pinged;
            FirstSeen = DateTime.UtcNow;
            LastSeen = pinged ? DateTime.UtcNow : DateTime.MinValue;
            LastQueried = DateTime.MinValue;
            FailCount = 0;
            Verified = false;
            NetworkEndPoint = new IPEndPoint(address, port);
        }

        /// <summary>
        /// Creates a node entry from a generic EndPoint (for I2P support).
        /// </summary>
        public NodeEntry(NodeId id, EndPoint endpoint, int port, int rtt = 0, bool pinged = false)
        {
            if (endpoint == null)
                throw new ArgumentNullException(nameof(endpoint));

            Id = id;
            Port = port;
            RttMs = rtt;
            Pinged = pinged;
            FirstSeen = DateTime.UtcNow;
            LastSeen = pinged ? DateTime.UtcNow : DateTime.MinValue;
            LastQueried = DateTime.MinValue;
            FailCount = 0;
            Verified = false;
            NetworkEndPoint = endpoint;

            // Extract Address for clearnet compat — null for I2P
            if (endpoint is IPEndPoint ipEp)
                Address = ipEp.Address;
        }

        /// <summary>
        /// Creates a node entry from an endpoint.
        /// </summary>
        public NodeEntry(NodeId id, IPEndPoint endpoint, int rtt = 0, bool pinged = false)
            : this(id, endpoint.Address, endpoint.Port, rtt, pinged)
        {
        }

        /// <summary>
        /// Creates a node entry from compact format (26 bytes: 20 ID + 4 IP + 2 port).
        /// </summary>
        public static NodeEntry FromCompact(ReadOnlySpan<byte> data)
        {
            if (data.Length < 26)
                throw new ArgumentException("Compact node info must be at least 26 bytes");

            var id = new NodeId(data.Slice(0, 20));
            var ip = new IPAddress(data.Slice(20, 4));
            int port = (data[24] << 8) | data[25];

            return new NodeEntry(id, ip, port);
        }

        /// <summary>
        /// Serializes this node to compact format (26 bytes).
        /// </summary>
        public byte[] ToCompact()
        {
            if (Address == null)
                throw new InvalidOperationException("Cannot encode non-IP node as IPv4 compact format. Use IDhtTransport.EncodeCompactNodeInfo instead.");

            var result = new byte[26];
            Id.Bytes.CopyTo(result.AsSpan(0, 20));
            Address.GetAddressBytes().CopyTo(result, 20);
            result[24] = (byte)(Port >> 8);
            result[25] = (byte)(Port & 0xFF);
            return result;
        }

        /// <summary>
        /// Checks if this node is "good" per BEP 5:
        /// - Has responded to one of our queries within the last 15 minutes, OR
        /// - Has ever responded and sent us a query within the last 15 minutes
        /// </summary>
        public bool IsGood(TimeSpan questionableTime)
        {
            if (!Pinged) return false;
            if (FailCount > 0) return false;

            var now = DateTime.UtcNow;
            var lastActive = LastSeen > LastQueried ? LastSeen : LastQueried;

            return (now - lastActive) < questionableTime;
        }

        /// <summary>
        /// Checks if this node is "questionable" per BEP 5:
        /// - Has not responded in the last 15 minutes
        /// </summary>
        public bool IsQuestionable(TimeSpan questionableTime)
        {
            if (!Pinged) return true;
            if (FailCount > 0) return true;

            var now = DateTime.UtcNow;
            return (now - LastSeen) >= questionableTime;
        }

        /// <summary>
        /// Checks if this node is "bad" (multiple consecutive failures).
        /// </summary>
        public bool IsBad(int maxFailCount)
        {
            return FailCount >= maxFailCount;
        }

        /// <summary>
        /// Checks if this node has been confirmed (pinged and responded).
        /// </summary>
        public bool IsConfirmed => Pinged && FailCount == 0;

        /// <summary>
        /// Called when a query is sent to this node.
        /// </summary>
        public void OnQuerySent()
        {
            LastQueried = DateTime.UtcNow;
        }

        /// <summary>
        /// Called when a response is received from this node.
        /// </summary>
        public void OnResponseReceived(int rttMs)
        {
            LastSeen = DateTime.UtcNow;
            Pinged = true;
            FailCount = 0;
            UpdateRtt(rttMs);
        }

        /// <summary>
        /// Called when a query to this node times out.
        /// </summary>
        public void OnQueryTimeout()
        {
            FailCount++;
        }

        /// <summary>
        /// Updates the RTT using exponential moving average.
        /// </summary>
        public void UpdateRtt(int newRtt)
        {
            if (newRtt <= 0) return;

            if (RttMs == 0)
            {
                RttMs = newRtt;
            }
            else
            {
                // Exponential moving average: RTT = 0.75 * old + 0.25 * new
                RttMs = (RttMs * 3 + newRtt) / 4;
            }
        }

        /// <summary>
        /// Compares nodes for routing table ordering.
        /// Prefers: verified, lower RTT, lower fail count, more recently seen.
        /// </summary>
        public int CompareTo(NodeEntry other)
        {
            if (other == null) return 1;

            // Prefer verified nodes
            if (Verified != other.Verified)
                return other.Verified.CompareTo(Verified);

            // Prefer nodes with lower fail count
            if (FailCount != other.FailCount)
                return FailCount.CompareTo(other.FailCount);

            // Prefer nodes with lower RTT
            if (RttMs != other.RttMs)
            {
                // 0 RTT means unknown, treat as worst
                int myRtt = RttMs == 0 ? int.MaxValue : RttMs;
                int otherRtt = other.RttMs == 0 ? int.MaxValue : other.RttMs;
                return myRtt.CompareTo(otherRtt);
            }

            // Prefer more recently seen nodes
            return other.LastSeen.CompareTo(LastSeen);
        }

        public override string ToString()
        {
            return $"{Id.ToShortHex()} @ {(Address != null ? $"{Address}:{Port}" : NetworkEndPoint?.ToString() ?? "unknown")} (RTT: {RttMs}ms, Fails: {FailCount})";
        }

        public override bool Equals(object obj)
        {
            return obj is NodeEntry other && Id.Equals(other.Id);
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }
}
