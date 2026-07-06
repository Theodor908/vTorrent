using System;

namespace vTorrent.Core.PeerCommunication.Extensions;

/// <summary>
/// Flags indicating peer capabilities in PEX messages.
/// Based on libtorrent's pex_flags.hpp (BEP 10).
/// </summary>
[Flags]
public enum PexFlags : byte
{
    /// <summary>
    /// No special flags.
    /// </summary>
    None = 0,

    /// <summary>
    /// Peer prefers encrypted connections (0x01).
    /// </summary>
    Encryption = 0x01,

    /// <summary>
    /// Peer is a seed - has all pieces (0x02).
    /// </summary>
    Seed = 0x02,

    /// <summary>
    /// Peer supports uTP protocol (0x04).
    /// This is a positive flag - absence doesn't mean no uTP support.
    /// </summary>
    Utp = 0x04,

    /// <summary>
    /// Peer supports hole punching protocol (0x08).
    /// Can be used as a rendezvous point for NAT traversal.
    /// </summary>
    Holepunch = 0x08
}
