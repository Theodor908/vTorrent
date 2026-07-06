namespace vTorrent.Abstractions.Enums;

/// <summary>
/// Status of a peer connection.
/// </summary>
public enum PeerConnectionStatus
{
    Discovered,    // Peer discovered but not yet attempted
    Connecting,    // Connection attempt in progress
    Connected,     // Currently connected
    Disconnected,  // Was connected, now disconnected
    Banned         // Banned due to protocol violations or poor performance
}
