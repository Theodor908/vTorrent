using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.TrackerCommunication.Models
{
    public class TrackerRequest
    {
        public byte[] InfoHash { get; }
        public byte[] PeerId { get; }
        public int Port { get; set; }
        public long Uploaded { get; set; }
        public long Downloaded { get; set; }
        public long Left { get; set; }
        public bool Compact { get; set; }
        public int NumWant {  get; set; }
        public TrackerEvent Event { get; set; }
        public string TrackerId { get; set; }
        public string Ip { get; set; }
        public bool NoPeerId { get; set; }
        public int PeerKey { get; set; }

        /// <summary>BEP 27: whether this request is for a private torrent.</summary>
        public bool IsPrivateTorrent { get; set; }

        /// <summary>Client's external IPv4 address (reported to private trackers per libtorrent convention).</summary>
        public string? Ipv4Address { get; set; }

        /// <summary>Client's external IPv6 address (reported to private trackers per libtorrent convention).</summary>
        public string? Ipv6Address { get; set; }

        public TrackerRequest(byte[] infoHash, byte[] peerId, int port)
        {
            if (infoHash == null || infoHash.Length != 20)
                throw new ArgumentException("InfoHash must be exactly 20 bytes");

            if (peerId == null || peerId.Length != 20)
                throw new ArgumentException("PeerId must be exactly 20 bytes");

            if (port < 1024 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1024 and 65535");

            InfoHash = infoHash;
            PeerId = peerId;
            Port = port;
            Compact = true; // Default to compact format
            NumWant = 50;   // Default number of peers
            Event = TrackerEvent.None;
            NoPeerId = false;

            TrackerId = string.Empty;
            Ip = string.Empty;
        }

        public static TrackerRequest CreateStarted(byte[] infoHash, byte[] peerId, int port, long left)
        {
            return new TrackerRequest(infoHash, peerId, port)
            {
                Event = TrackerEvent.Started,
                Left = left,
                Uploaded = 0,
                Downloaded = 0
            };
        }

        public static TrackerRequest CreateStopped(byte[] infoHash, byte[] peerId, int port,
            long uploaded, long downloaded, long left)
        {
            return new TrackerRequest(infoHash, peerId, port)
            {
                Event = TrackerEvent.Stopped,
                Uploaded = uploaded,
                Downloaded = downloaded,
                Left = left
            };
        }

        public static TrackerRequest CreateCompleted(byte[] infoHash, byte[] peerId, int port,
            long uploaded, long downloaded)
        {
            return new TrackerRequest(infoHash, peerId, port)
            {
                Event = TrackerEvent.Completed,
                Uploaded = uploaded,
                Downloaded = downloaded,
                Left = 0
            };
        }

        public static TrackerRequest CreateRegular(byte[] infoHash, byte[] peerId, int port,
            long uploaded, long downloaded, long left)
        {
            return new TrackerRequest(infoHash, peerId, port)
            {
                Event = TrackerEvent.None,
                Uploaded = uploaded,
                Downloaded = downloaded,
                Left = left
            };
        }

        public TrackerRequest WithUpdatedStats(long uploaded, long downloaded, long left)
        {
            return new TrackerRequest(InfoHash, PeerId, Port)
            {
                Uploaded = uploaded,
                Downloaded = downloaded,
                Left = left,
                Event = TrackerEvent.None,
                Compact = Compact,
                NumWant = NumWant,
                TrackerId = TrackerId,
                Ip = Ip,
                NoPeerId = NoPeerId,
                IsPrivateTorrent = IsPrivateTorrent,
                Ipv4Address = Ipv4Address,
                Ipv6Address = Ipv6Address
            };
        }

        public override string ToString()
        {
            return $"TrackerRequest [Event: {Event}, Port: {Port}, " +
                   $"Up: {Uploaded}, Down: {Downloaded}, Left: {Left}]";
        }

    }
}
