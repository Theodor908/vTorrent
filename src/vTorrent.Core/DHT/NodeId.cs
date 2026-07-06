using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace vTorrent.Core.DHT
{
    /// <summary>
    /// Represents a 160-bit DHT node ID.
    /// The node ID uses the same space as BitTorrent info_hashes (SHA-1 hashes).
    /// Implements Kademlia XOR-based distance metric per BEP 5.
    /// Node ID generation follows BEP 42 using CRC32-C (Castagnoli polynomial).
    /// </summary>
    public readonly struct NodeId : IEquatable<NodeId>, IComparable<NodeId>
    {
        public const int ByteLength = 20;
        public const int BitLength = 160;

        // CRC32-C (Castagnoli) lookup table - same polynomial as libtorrent
        private static readonly uint[] Crc32CTable = GenerateCrc32CTable();

        // BEP-42 IP masks for node ID generation
        private static readonly byte[] IPv4Mask = { 0x03, 0x0f, 0x3f, 0xff };
        private static readonly byte[] IPv6Mask = { 0x01, 0x03, 0x07, 0x0f, 0x1f, 0x3f, 0x7f, 0xff };

        private static uint[] GenerateCrc32CTable()
        {
            // CRC32-C uses the Castagnoli polynomial (0x1EDC6F41)
            // Reversed form: 0x82F63B78
            const uint polynomial = 0x82F63B78;
            var table = new uint[256];

            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? polynomial : 0);
                }
                table[i] = crc;
            }

            return table;
        }

        private static uint ComputeCrc32C(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc = Crc32CTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return crc ^ 0xFFFFFFFF;
        }

        private readonly byte[] _bytes;

        /// <summary>
        /// Gets the raw 20-byte node ID.
        /// </summary>
        public ReadOnlySpan<byte> Bytes => _bytes ?? Zero._bytes;

        /// <summary>
        /// Zero node ID (all zeros).
        /// </summary>
        public static readonly NodeId Zero = new(new byte[ByteLength]);

        /// <summary>
        /// Maximum node ID (all ones).
        /// </summary>
        public static readonly NodeId Max = new(CreateMaxBytes());

        private static byte[] CreateMaxBytes()
        {
            var bytes = new byte[ByteLength];
            Array.Fill(bytes, (byte)0xFF);
            return bytes;
        }

        /// <summary>
        /// Creates a NodeId from a 20-byte array.
        /// </summary>
        public NodeId(byte[] bytes)
        {
            if (bytes == null || bytes.Length != ByteLength)
                throw new ArgumentException($"Node ID must be exactly {ByteLength} bytes", nameof(bytes));

            _bytes = new byte[ByteLength];
            bytes.CopyTo(_bytes, 0);
        }

        /// <summary>
        /// Creates a NodeId from a ReadOnlySpan.
        /// </summary>
        public NodeId(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != ByteLength)
                throw new ArgumentException($"Node ID must be exactly {ByteLength} bytes", nameof(bytes));

            _bytes = bytes.ToArray();
        }

        /// <summary>
        /// Creates a NodeId from a hex string (40 characters).
        /// </summary>
        public static NodeId FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length != ByteLength * 2)
                throw new ArgumentException($"Hex string must be exactly {ByteLength * 2} characters", nameof(hex));

            var bytes = new byte[ByteLength];
            for (int i = 0; i < ByteLength; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return new NodeId(bytes);
        }

        /// <summary>
        /// Generates a random node ID.
        /// </summary>
        public static NodeId GenerateRandom()
        {
            var bytes = new byte[ByteLength];
            RandomNumberGenerator.Fill(bytes);
            return new NodeId(bytes);
        }

        /// <summary>
        /// Generates a node ID based on IP address (BEP 42 compliant).
        /// This helps prevent Sybil attacks by binding node IDs to IP addresses.
        /// Uses CRC32-C (Castagnoli) polynomial, matching libtorrent's implementation.
        /// </summary>
        /// <param name="ip">The IP address to derive the node ID from.</param>
        /// <param name="r">Optional 'r' parameter (0-255). If null, a random value is generated.</param>
        /// <returns>A BEP-42 compliant node ID.</returns>
        public static NodeId GenerateFromIp(IPAddress ip, byte? r = null)
        {
            var ipBytes = ip.GetAddressBytes();
            var nodeBytes = new byte[ByteLength];

            // Generate random 'r' parameter if not provided
            // For IPv4: r uses only 3 bits (0-7)
            // For IPv6: r uses 8 bits (0-255)
            byte rValue;
            if (r.HasValue)
            {
                rValue = r.Value;
            }
            else
            {
                bool isIpv4 = ip.AddressFamily == AddressFamily.InterNetwork;
                rValue = (byte)(isIpv4 ? RandomNumberGenerator.GetInt32(8) : RandomNumberGenerator.GetInt32(256));
            }

            // Select appropriate mask based on IP version
            var mask = ip.AddressFamily == AddressFamily.InterNetworkV6 ? IPv6Mask : IPv4Mask;

            // Apply IP mask per BEP-42
            // For IPv4: mask first 4 bytes
            // For IPv6: mask first 8 bytes
            int maskLen = Math.Min(ipBytes.Length, mask.Length);
            Span<byte> masked = stackalloc byte[maskLen];
            for (int i = 0; i < maskLen; i++)
            {
                masked[i] = (byte)(ipBytes[i] & mask[i]);
            }

            // Incorporate 'r' into first byte (3 bits for IPv4, shifted to high position)
            // libtorrent: ip[0] |= (r << 5)
            masked[0] |= (byte)((rValue << 5) & 0xFF);

            // Compute CRC32-C of masked IP (libtorrent uses crc32c_32)
            uint crc = ComputeCrc32C(masked);

            // First 3 bytes from CRC32-C (big-endian, top 21 bits)
            // libtorrent:
            //   id[0] = (c >> 24) & 0xff
            //   id[1] = (c >> 16) & 0xff
            //   id[2] = ((c >> 8) & 0xf8) | random(0x7)
            nodeBytes[0] = (byte)((crc >> 24) & 0xFF);
            nodeBytes[1] = (byte)((crc >> 16) & 0xFF);
            nodeBytes[2] = (byte)(((crc >> 8) & 0xF8) | (RandomNumberGenerator.GetInt32(8) & 0x07));

            // Bytes 3-18 are random (libtorrent: for (int i = 3; i < 19; ++i) id[i] = random(0xff))
            RandomNumberGenerator.Fill(nodeBytes.AsSpan(3, 16));

            // Last byte (19) contains low 8 bits of 'r' parameter
            // This is used for ID verification
            nodeBytes[19] = rValue;

            return new NodeId(nodeBytes);
        }

        /// <summary>
        /// Verifies that a node ID is valid for the given IP address per BEP-42.
        /// </summary>
        /// <param name="nodeId">The node ID to verify.</param>
        /// <param name="ip">The IP address the node ID should match.</param>
        /// <returns>True if the node ID is valid for this IP address.</returns>
        public static bool VerifyId(NodeId nodeId, IPAddress ip)
        {
            // Extract 'r' from last byte of node ID
            byte r = nodeId.Bytes[19];

            // Regenerate expected ID from IP and r
            var expected = GenerateFromIp(ip, r);

            // Check first 3 bytes match per BEP-42:
            // Byte 0: must match exactly
            // Byte 1: must match exactly
            // Byte 2: only top 5 bits must match (mask 0xF8)
            if (nodeId.Bytes[0] != expected.Bytes[0])
                return false;
            if (nodeId.Bytes[1] != expected.Bytes[1])
                return false;
            if ((nodeId.Bytes[2] & 0xF8) != (expected.Bytes[2] & 0xF8))
                return false;

            return true;
        }

        /// <summary>
        /// Calculates the XOR distance between two node IDs.
        /// In Kademlia, closer nodes have smaller XOR distances.
        /// </summary>
        public static NodeId Distance(NodeId a, NodeId b)
        {
            var result = new byte[ByteLength];
            var aBytes = a.Bytes;
            var bBytes = b.Bytes;

            for (int i = 0; i < ByteLength; i++)
            {
                result[i] = (byte)(aBytes[i] ^ bBytes[i]);
            }

            return new NodeId(result);
        }

        /// <summary>
        /// Compares distances: returns true if distance(n1, ref) < distance(n2, ref).
        /// </summary>
        public static bool CompareByDistance(NodeId n1, NodeId n2, NodeId reference)
        {
            var d1 = Distance(n1, reference);
            var d2 = Distance(n2, reference);
            return d1.CompareTo(d2) < 0;
        }

        /// <summary>
        /// Returns the number of leading zero bits in the XOR distance.
        /// This determines which bucket a node belongs to.
        /// Higher values mean the node is "closer" to us.
        /// </summary>
        public static int DistanceExp(NodeId a, NodeId b)
        {
            var distance = Distance(a, b);
            return BitLength - 1 - distance.LeadingZeroBits();
        }

        /// <summary>
        /// Counts the number of leading zero bits in this node ID.
        /// </summary>
        public int LeadingZeroBits()
        {
            var bytes = Bytes;
            int count = 0;

            for (int i = 0; i < ByteLength; i++)
            {
                if (bytes[i] == 0)
                {
                    count += 8;
                }
                else
                {
                    // Count leading zeros in this byte
                    byte b = bytes[i];
                    while ((b & 0x80) == 0)
                    {
                        count++;
                        b <<= 1;
                    }
                    break;
                }
            }

            return count;
        }

        /// <summary>
        /// Gets the bit value at the specified index (0 = MSB).
        /// </summary>
        public bool GetBit(int index)
        {
            if (index < 0 || index >= BitLength)
                throw new ArgumentOutOfRangeException(nameof(index));

            int byteIndex = index / 8;
            int bitIndex = 7 - (index % 8);
            return (Bytes[byteIndex] & (1 << bitIndex)) != 0;
        }

        /// <summary>
        /// Creates a node ID with a specific prefix (for bucket refresh).
        /// </summary>
        public static NodeId GenerateWithPrefix(NodeId prefix, int commonBits)
        {
            var result = new byte[ByteLength];
            RandomNumberGenerator.Fill(result);

            var prefixBytes = prefix.Bytes;
            int fullBytes = commonBits / 8;
            int remainingBits = commonBits % 8;

            // Copy full bytes
            for (int i = 0; i < fullBytes; i++)
            {
                result[i] = prefixBytes[i];
            }

            // Handle partial byte
            if (remainingBits > 0 && fullBytes < ByteLength)
            {
                byte mask = (byte)(0xFF << (8 - remainingBits));
                result[fullBytes] = (byte)((prefixBytes[fullBytes] & mask) | (result[fullBytes] & ~mask));
            }

            return new NodeId(result);
        }

        /// <summary>
        /// Returns the hex string representation of the node ID.
        /// </summary>
        public string ToHex()
        {
            return Convert.ToHexString(Bytes).ToLowerInvariant();
        }

        /// <summary>
        /// Returns a shortened display string (first 8 hex chars).
        /// </summary>
        public string ToShortHex()
        {
            return ToHex().Substring(0, 8) + "...";
        }

        public bool Equals(NodeId other)
        {
            return Bytes.SequenceEqual(other.Bytes);
        }

        public override bool Equals(object obj)
        {
            return obj is NodeId other && Equals(other);
        }

        public override int GetHashCode()
        {
            var bytes = Bytes;
            // Use first 4 bytes for hash (they're usually unique enough)
            return BinaryPrimitives.ReadInt32BigEndian(bytes);
        }

        public int CompareTo(NodeId other)
        {
            var a = Bytes;
            var b = other.Bytes;

            for (int i = 0; i < ByteLength; i++)
            {
                int cmp = a[i].CompareTo(b[i]);
                if (cmp != 0) return cmp;
            }

            return 0;
        }

        public static bool operator ==(NodeId left, NodeId right) => left.Equals(right);
        public static bool operator !=(NodeId left, NodeId right) => !left.Equals(right);
        public static bool operator <(NodeId left, NodeId right) => left.CompareTo(right) < 0;
        public static bool operator >(NodeId left, NodeId right) => left.CompareTo(right) > 0;
        public static bool operator <=(NodeId left, NodeId right) => left.CompareTo(right) <= 0;
        public static bool operator >=(NodeId left, NodeId right) => left.CompareTo(right) >= 0;

        public override string ToString() => ToHex();

        /// <summary>
        /// Check if the node ID is all zeros.
        /// </summary>
        public bool IsZero()
        {
            var bytes = Bytes;
            for (int i = 0; i < ByteLength; i++)
            {
                if (bytes[i] != 0) return false;
            }
            return true;
        }
    }
}
