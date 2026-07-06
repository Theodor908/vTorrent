using System;

namespace vTorrent.Core.PeerCommunication.Extensions;

/// <summary>
/// Flags indicating how a peer was discovered.
/// Based on libtorrent's peer_info::peer_source_flags.
/// A peer can be discovered from multiple sources.
/// </summary>
[Flags]
public enum PeerSource
{
    /// <summary>
    /// Unknown source.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Peer was received from tracker announce.
    /// </summary>
    Tracker = 0x01,

    /// <summary>
    /// Peer was received from DHT.
    /// </summary>
    Dht = 0x02,

    /// <summary>
    /// Peer was received from Peer Exchange (PEX).
    /// </summary>
    Pex = 0x04,

    /// <summary>
    /// Peer was received from Local Service Discovery (LSD).
    /// </summary>
    Lsd = 0x08,

    /// <summary>
    /// Peer was loaded from resume data.
    /// </summary>
    ResumeData = 0x10,

    /// <summary>
    /// Peer connected to us (incoming connection).
    /// </summary>
    Incoming = 0x20,

    /// <summary>
    /// Peer was manually added.
    /// </summary>
    Manual = 0x40
}
