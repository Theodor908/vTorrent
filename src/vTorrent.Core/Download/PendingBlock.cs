using System;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Download;

public class PendingBlock
{
    public IPeerConnection Peer { get; init; }
    public int PieceIndex { get; init; }
    public int Begin { get; init; }
    public int Length { get; init; }
    public DateTime RequestedAt { get; init; }

    /// <summary>
    /// True if this is a "busy" block request (duplicate request in endgame mode).
    /// </summary>
    public bool IsBusy { get; init; }
}