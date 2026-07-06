using System;

namespace vTorrent.Abstractions.Enums;

/// <summary>
/// Per-protocol flags for UDP send operations. Controls proxy routing
/// when SOCKS5 UDP association is active in UdpSocketManager.
/// </summary>
[Flags]
public enum UdpSendFlags
{
    /// <summary>
    /// No flag — DHT traffic (untagged).
    /// </summary>
    None = 0,

    /// <summary>
    /// uTP peer connection traffic.
    /// </summary>
    PeerConnection = 1,

    /// <summary>
    /// UDP tracker protocol traffic.
    /// </summary>
    TrackerConnection = 2
}
