using System;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.PeerCommunication.Events
{
    public class PeerMessageEventArgs : EventArgs
    {
        public IPeerConnection Peer { get; }
        public PeerMessage Message { get; }

        public PeerMessageEventArgs(IPeerConnection peer, PeerMessage message)
        {
            Peer = peer;
            Message = message;
        }
    }
}
