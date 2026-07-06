using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.PeerCommunication.Exceptions
{
    public class PeerProtocolException : Exception
    {

        public string PeerEndPoint { get; }

        public PeerProtocolException(string message) : base(message) 
        { 

        }

        public PeerProtocolException(string message, Exception innerException) 
        { 

        }

        public PeerProtocolException(string message, string peerEndPoint)
            : base(message)
        {
            PeerEndPoint = peerEndPoint;
        }

        public PeerProtocolException(string message, string peerEndPoint, Exception innerException)
            : base(message, innerException)
        {
            PeerEndPoint = peerEndPoint;
        }

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(PeerEndPoint))
            {
                return $"[Peer: {PeerEndPoint}] {base.ToString()}";
            }
            return base.ToString();
        }

    }
}
