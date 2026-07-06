using System;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Upload;

public class UploadRequest
{
    public IPeerConnection Peer { get; init; }
    public int PieceIndex { get; init; }
    public int Begin { get; init; }
    public int Length { get; init; }
    public DateTime RequestedAt { get; init; }
}
