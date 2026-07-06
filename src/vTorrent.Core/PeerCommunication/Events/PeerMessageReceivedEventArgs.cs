using System;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.PeerCommunication.Events
{
    public class PeerMessageReceivedEventArgs : EventArgs
    {
        public PeerMessage Message { get; }

        public PeerMessageReceivedEventArgs(PeerMessage message)
        {
            Message = message;
        }
    }
}
