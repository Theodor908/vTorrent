namespace vTorrent.Core.Events;

/// <summary>
/// Base class for orchestrator-level peer events.
/// </summary>
public class PeerEventArgs : TorrentEventArgs
{
    public string PeerEndpoint { get; init; } = "";
    public string? ClientName { get; init; }
}

/// <summary>
/// Raised when a peer connects to a torrent.
/// </summary>
public class PeerConnectedEventArgs : PeerEventArgs { }

/// <summary>
/// Raised when a peer disconnects from a torrent.
/// </summary>
public class PeerDisconnectedEventArgs : PeerEventArgs
{
    public string Reason { get; init; } = "";
}
