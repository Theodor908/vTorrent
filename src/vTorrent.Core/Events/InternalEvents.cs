using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Core.Events;

public record PeerInterestedEvent(IPeerConnection Peer);
public record PeerNotInterestedEvent(IPeerConnection Peer);
public record PieceCompletedEvent(int PieceIndex);
public record BlockUploadedEvent(int Bytes, IPeerConnection Peer);
public record BlockDownloadedEvent(int Bytes, IPeerConnection Peer);
