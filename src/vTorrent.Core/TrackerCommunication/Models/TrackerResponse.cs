using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.TrackerCommunication.Models
{
    public class TrackerResponse
    {
        public int Interval { get; set; }

        public int? MinInterval { get; set; }

        public string TrackerId { get; set; }

        public int Complete { get; set; }

        public int Incomplete { get; set; }

        public List<TrackerPeer> Peers { get; set; }

        public string WarningMessage { get; set; }

        public string FailureReason { get; set; }

        public DateTime ReceivedAt { get; set; }

        public string TrackerUrl { get; set; }

        /// <summary>
        /// BEP 24: External IP address as reported by the tracker.
        /// Packed binary format (4 bytes IPv4, 16 bytes IPv6).
        /// </summary>
        public System.Net.IPAddress? ExternalIp { get; set; }

        public bool IsSuccess => string.IsNullOrEmpty(FailureReason);

        public TrackerResponse()
        {
            Peers = new List<TrackerPeer>();
            ReceivedAt = DateTime.UtcNow;
        }

        public static TrackerResponse CreateFailure(string failureReason, string trackerUrl = null)
        {
            return new TrackerResponse
            {
                FailureReason = failureReason,
                TrackerUrl = trackerUrl,
                ReceivedAt = DateTime.UtcNow
            };
        }

        public static TrackerResponse CreateSuccess(int interval, List<TrackerPeer> peers,
            int complete = 0, int incomplete = 0, string trackerUrl = null)
        {
            return new TrackerResponse
            {
                Interval = interval,
                Peers = peers ?? new List<TrackerPeer>(),
                Complete = complete,
                Incomplete = incomplete,
                TrackerUrl = trackerUrl,
                ReceivedAt = DateTime.UtcNow
            };
        }

        public DateTime GetNextAnnounceTime()
        {
            int intervalToUse = MinInterval ?? Interval;
            return ReceivedAt.AddSeconds(intervalToUse);
        }

        public override string ToString()
        {
            if (!IsSuccess)
                return $"TrackerResponse [Failed: {FailureReason}]";

            return $"TrackerResponse [Peers: {Peers.Count}, Seeders: {Complete}, " +
                   $"Leechers: {Incomplete}, Interval: {Interval}s]";
        }
    }
}
