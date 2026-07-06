namespace vTorrent.Core.DHT;

public static class DhtConstants
{
    public const int BucketSize = 8;
    public const int BucketRefreshIntervalMs = 900_000;
    public const int NodeQuestionableTimeMs = 900_000;
    public const int TokenRefreshIntervalMs = 300_000;
    public const int PeerExpirationMs = 1_800_000;
    public const int TickIntervalMs = 5_000;
    public const int MinLookupIntervalMs = 4_000;
    public const int LowPeerThreshold = 5;
    public const int InitialBoostLookups = 3;
    public const int BoostLookupIntervalMs = 2_000;
    public const int MaxIncomingRequestsPerSecond = 50;
    public const int RateLimitAveragingSeconds = 10;
}
