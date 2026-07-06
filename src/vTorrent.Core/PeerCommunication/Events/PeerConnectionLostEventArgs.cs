using System;

namespace vTorrent.Core.PeerCommunication.Events
{
    public class PeerConnectionLostEventArgs : EventArgs
    {
        public Exception Exception { get; }
        public string Reason { get; }

        public PeerConnectionLostEventArgs(string reason, Exception exception = null)
        {
            Reason = reason;
            Exception = exception;
        }
    }
}
