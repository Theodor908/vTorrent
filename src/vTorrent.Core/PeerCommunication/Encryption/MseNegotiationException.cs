using System;

namespace vTorrent.Core.PeerCommunication.Encryption;

/// <summary>
/// Thrown when MSE/PE protocol negotiation fails.
/// </summary>
public class MseNegotiationException : Exception
{
    public MseNegotiationException(string message) : base(message) { }
    public MseNegotiationException(string message, Exception inner) : base(message, inner) { }
}
