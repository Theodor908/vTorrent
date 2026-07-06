using System;
using System.Net;
using System.Runtime.CompilerServices;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Network.I2P;

/// <summary>
/// Debug-only assertion helpers that detect IP leaks from I2P-only contexts.
/// Compiled out in Release builds via #if DEBUG.
/// </summary>
public static class I2pLeakDetector
{
#if DEBUG
    public static void AssertI2pEndPoint(EndPoint ep, [CallerMemberName] string caller = "")
    {
        if (ep is not I2pEndPoint)
            throw new I2pLeakException($"LEAK: {caller} received non-I2P endpoint: {ep}");
    }

    public static void AssertI2pPeer(PeerInfo peer, [CallerMemberName] string caller = "")
    {
        if (!peer.IsI2p)
            throw new I2pLeakException($"LEAK: {caller} received clearnet peer: {peer}");
    }
#else
    public static void AssertI2pEndPoint(EndPoint ep, [CallerMemberName] string caller = "") { }
    public static void AssertI2pPeer(PeerInfo peer, [CallerMemberName] string caller = "") { }
#endif
}

public sealed class I2pLeakException : Exception
{
    public I2pLeakException(string message) : base(message) { }
}
