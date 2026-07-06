using System;
using System.Collections.Generic;
using vTorrent.Core.PeerCommunication.Extensions;

namespace vTorrent.Core.PeerCommunication.Events;

/// <summary>
/// Event arguments for when peers are discovered via PEX.
/// </summary>
public class PexPeersDiscoveredEventArgs : EventArgs
{
    /// <summary>
    /// The newly discovered peers.
    /// </summary>
    public IReadOnlyList<PexPeerEntry> Peers { get; }

    public PexPeersDiscoveredEventArgs(IReadOnlyList<PexPeerEntry> peers)
    {
        Peers = peers ?? throw new ArgumentNullException(nameof(peers));
    }
}
