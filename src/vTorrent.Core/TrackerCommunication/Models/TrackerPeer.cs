using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.TrackerCommunication.Models
{
    public class TrackerPeer
    {
        public IPAddress Ip { get; set; }

        public int Port { get; set; }

        public byte[] PeerId { get; set; }

        public I2pDestination? I2pDestination { get; set; }

        public bool IsI2p => I2pDestination != null;

        public IPEndPoint EndPoint => new IPEndPoint(Ip, Port);

        public TrackerPeer(IPAddress ip, int port, byte[] peerId = null)
        {
            Ip = ip ?? throw new ArgumentNullException(nameof(ip));

            if (port < 0 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));

            Port = port;
            PeerId = peerId;
        }

        private TrackerPeer(I2pDestination destination)
        {
            I2pDestination = destination ?? throw new ArgumentNullException(nameof(destination));
            Ip = System.Net.IPAddress.None;
            Port = 0;
        }
        public static TrackerPeer FromCompact(byte[] data, int offset = 0)
        {
            if (data == null || data.Length < offset + 6)
                throw new ArgumentException("Invalid compact peer data");

            // First 4 bytes: IP (big-endian)
            byte[] ipBytes = new byte[4];
            Buffer.BlockCopy(data, offset, ipBytes, 0, 4);
            IPAddress ip = new IPAddress(ipBytes);

            // Next 2 bytes: port (big-endian)
            int port = data[offset + 4] << 8 | data[offset + 5];

            return new TrackerPeer(ip, port);
        }

        public static List<TrackerPeer> FromCompactList(byte[] data)
        {
            if (data == null || data.Length % 6 != 0)
                throw new ArgumentException("Invalid compact peer list");
            
            var peers = new List<TrackerPeer>();
            
            int peerCount = data.Length / 6;

            for (int i = 0; i < peerCount; i++)
            {
                try
                {
                    var peer = FromCompact(data, i * 6);

                    if (peer.Port == 0)
                    {
                        continue;
                    }

                    if (peer.Ip.Equals(IPAddress.None) || peer.Ip.Equals(IPAddress.Broadcast) ||
                        peer.Ip.Equals(IPAddress.Any))
                    {
                        continue;
                    }
                    
                    peers.Add(peer);
                }
                catch (ArgumentException)
                {
                    // Skip invalid peer entries, don't fail entire list
                    continue;
                }
                catch (IndexOutOfRangeException)
                {
                    // Skip malformed peer data, don't fail entire list
                    continue;
                }
            }

            return peers;
        }

        public override bool Equals(object obj)
        {
            if (obj is not TrackerPeer other) return false;
            if (IsI2p && other.IsI2p)
                return I2pDestination!.Equals(other.I2pDestination);
            if (IsI2p != other.IsI2p) return false;
            return Ip.Equals(other.Ip) && Port == other.Port;
        }

        public override int GetHashCode() =>
            IsI2p ? I2pDestination!.GetHashCode() : HashCode.Combine(Ip, Port);

        public override string ToString()
        {
            if (IsI2p)
                return $"i2p:{I2pDestination}";
            string peerIdStr = PeerId != null ? $" [{Encoding.ASCII.GetString(PeerId)}]" : "";
            return $"{Ip}:{Port}{peerIdStr}";
        }

        public static TrackerPeer FromI2pCompact(byte[] data, int offset = 0)
        {
            if (data == null || data.Length < offset + 32)
                throw new ArgumentException("Invalid I2P compact peer data — need 32 bytes");

            var hashBytes = new byte[32];
            Buffer.BlockCopy(data, offset, hashBytes, 0, 32);
            var dest = I2pDestination.FromHash(hashBytes);
            return new TrackerPeer(dest);
        }

        public static List<TrackerPeer> FromI2pCompactList(byte[] data)
        {
            if (data == null || data.Length == 0)
                return new List<TrackerPeer>();

            if (data.Length % 32 != 0)
                throw new ArgumentException($"Invalid I2P compact peer list length {data.Length} — not divisible by 32");

            var peers = new List<TrackerPeer>();
            int peerCount = data.Length / 32;

            for (int i = 0; i < peerCount; i++)
            {
                try
                {
                    peers.Add(FromI2pCompact(data, i * 32));
                }
                catch (ArgumentException)
                {
                    continue;
                }
            }

            return peers;
        }
    }
}
