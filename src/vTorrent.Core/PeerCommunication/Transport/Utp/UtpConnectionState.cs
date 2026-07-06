namespace vTorrent.Core.PeerCommunication.Transport.Utp;

public enum UtpConnectionState
{
    None,
    SynSent,
    SynRecv,
    Connected,
    FinSent,
    Closed,
    Reset
}
