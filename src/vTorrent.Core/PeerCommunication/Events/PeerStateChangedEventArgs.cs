using System;

namespace vTorrent.Core.PeerCommunication.Events
{
    public class PeerStateChangedEventArgs : EventArgs
    {
        public bool IsChoked { get; }
        public bool IsInterested { get; }
        public bool IsChoking { get; }
        public bool IsPeerInterested { get; }

        public PeerStateChangedEventArgs(bool isChoked, bool isInterested, bool isChoking, bool isPeerInterested)
        {
            IsChoked = isChoked;
            IsInterested = isInterested;
            IsChoking = isChoking;
            IsPeerInterested = isPeerInterested;
        }
    }
}
