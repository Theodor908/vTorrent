namespace vTorrent.Core.PeerCommunication;

public static class PeerConstants
{
    public const int BlockSize = 16_384;
    public const bool TcpNoDelay = true;
    public const bool TcpKeepAlive = true;
    public const int TcpKeepAliveTimeSeconds = 60;
    public const int TcpKeepAliveIntervalSeconds = 10;
    public const bool LingerOnClose = false;
    public const int LingerTimeoutSeconds = 0;
    public const int KeepAliveIntervalSeconds = 120;
    public const int MaxRequestsPerPeer = 250;
    public const int ConnectionsPerSecond = 30;
    public const int HolepunchMaxConcurrent = 5;
    public const int HolepunchCooldownSeconds = 30;
    public const int SendBufferSize = 0;
    public const int ReceiveBufferSize = 0;
}
