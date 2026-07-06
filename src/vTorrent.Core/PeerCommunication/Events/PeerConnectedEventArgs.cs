using System;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.PeerCommunication.Events
{
    public class PeerConnectedEventArgs : EventArgs
    {
        public IPeerConnection Peer { get; }

        public PeerConnectedEventArgs(IPeerConnection peer)
        {
            Peer = peer;
        }
    }
}
