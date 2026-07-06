using System;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Events;

/// <summary>
/// Raised when a peer's choke state changes (choked or unchoked).
/// Replaces the identical PeerChokedEventArgs and PeerUnchokedEventArgs.
/// </summary>
public class PeerChokeChangedEventArgs : EventArgs
{
    public IPeerConnection Peer { get; }
    public bool IsChoked { get; }

    public PeerChokeChangedEventArgs(IPeerConnection peer, bool isChoked)
    {
        Peer = peer;
        IsChoked = isChoked;
    }
}
