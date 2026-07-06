namespace vTorrent.Core.TrackerCommunication;

public static class TrackerConstants
{
    public const bool UseCompactFormat = true;
    public const bool EnableScrape = true;
    public const bool EnableHttpConnectionPooling = true;
    public const int MaxConnectionsPerServer = 4;
    public const int PooledConnectionLifetimeMinutes = 5;
    public const int PooledConnectionIdleTimeoutMinutes = 2;
    public const bool EnableHttp2 = true;
    public const int MaxAnnounceHistory = 10;
    public const int MaxHttpResponseSize = 1_048_576;
    public const int DefaultAnnounceInterval = 1_800;
    public const int EarlyReturnTimeoutSeconds = 5;
}
