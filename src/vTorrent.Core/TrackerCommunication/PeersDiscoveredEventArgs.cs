using System;
using System.Collections.Generic;
using vTorrent.Core.TrackerCommunication.Models;

namespace vTorrent.Core.TrackerCommunication;

public class PeersDiscoveredEventArgs : EventArgs
{
    public string TrackerUrl { get; }
    public IReadOnlyList<TrackerPeer> Peers { get; }
    public int Seeders { get; }
    public int Leechers { get; }
    public DateTime DiscoveredAt { get; }

    public PeersDiscoveredEventArgs(string trackerUrl, IReadOnlyList<TrackerPeer> peers, int seeders, int leechers)
    {
        TrackerUrl = trackerUrl;
        Peers = peers;
        Seeders = seeders;
        Leechers = leechers;
        DiscoveredAt = DateTime.UtcNow;
    }
}