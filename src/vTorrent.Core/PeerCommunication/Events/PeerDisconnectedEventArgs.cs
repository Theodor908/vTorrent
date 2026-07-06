using System;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.PeerCommunication.Events
{
    public class PeerDisconnectedEventArgs : EventArgs
    {
        public PeerInfo PeerInfo { get; }
        public string Reason { get; }

        public PeerDisconnectedEventArgs(PeerInfo peerInfo, string reason)
        {
            PeerInfo = peerInfo;
            Reason = reason;
        }
    }
}
