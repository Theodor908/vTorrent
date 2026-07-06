using System;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// Session-wide statistics aggregated across all torrents.
/// Follows libtorrent's session_stats model.
/// </summary>
public class SessionStatistics
{
    #region Transfer Statistics

    /// <summary>
    /// Total bytes sent across all connections (including protocol overhead).
    /// Used by SessionOverviewViewModel.
    /// </summary>
    public long TotalBytesSent { get; set; }

    /// <summary>
    /// Total bytes received across all connections (including protocol overhead).
    /// Used by SessionOverviewViewModel.
    /// </summary>
    public long TotalBytesReceived { get; set; }

    #endregion

    #region Rate Statistics

    /// <summary>
    /// Current global download rate in bytes/second (including protocol overhead).
    /// Used by SessionOverviewViewModel.
    /// </summary>
    public int GlobalDownloadRate { get; set; }

    /// <summary>
    /// Current global upload rate in bytes/second (including protocol overhead).
    /// Used by SessionOverviewViewModel.
    /// </summary>
    public int GlobalUploadRate { get; set; }

    #endregion

    #region Torrent Counts

    /// <summary>
    /// Number of torrents currently downloading
    /// </summary>
    public int DownloadingTorrents { get; set; }

    /// <summary>
    /// Number of torrents currently seeding
    /// </summary>
    public int SeedingTorrents { get; set; }

    /// <summary>
    /// Number of paused torrents
    /// </summary>
    public int PausedTorrents { get; set; }

    /// <summary>
    /// Number of torrents in checking state
    /// </summary>
    public int CheckingTorrents { get; set; }

    /// <summary>
    /// Number of torrents in error state
    /// </summary>
    public int ErrorTorrents { get; set; }

    /// <summary>
    /// Number of upload-only torrents (finished, seeding)
    /// </summary>
    public int UploadOnlyTorrents { get; set; }

    /// <summary>
    /// Total number of torrents in the session
    /// </summary>
    public int TotalTorrents => DownloadingTorrents + SeedingTorrents + PausedTorrents +
                                 CheckingTorrents + ErrorTorrents;

    /// <summary>
    /// Number of active torrents (downloading or seeding)
    /// </summary>
    public int ActiveTorrents => DownloadingTorrents + SeedingTorrents;

    #endregion

    #region Connection Statistics

    /// <summary>
    /// Total number of connected peers across all torrents
    /// </summary>
    public int TotalPeersConnected { get; set; }

    /// <summary>
    /// Total number of connected seeds (peers with 100% of torrents) across all torrents
    /// </summary>
    public int TotalConnectedSeeds { get; set; }

    /// <summary>
    /// Number of half-open connections (connection in progress)
    /// </summary>
    public int HalfOpenConnections { get; set; }

    /// <summary>
    /// Number of peers we're uploading to
    /// </summary>
    public int UploadingPeers { get; set; }

    /// <summary>
    /// Number of peers we're downloading from
    /// </summary>
    public int DownloadingPeers { get; set; }

    /// <summary>
    /// Number of unchoked peers
    /// </summary>
    public int UnchokedPeers { get; set; }

    /// <summary>
    /// Number of peer connection attempts made
    /// </summary>
    public int ConnectionAttempts { get; set; }

    /// <summary>
    /// Number of connections rejected (limit, banned, etc.)
    /// </summary>
    public int ConnectionsRejected { get; set; }

    #endregion

    #region DHT Statistics

    /// <summary>
    /// Number of nodes in DHT routing table
    /// </summary>
    public int DhtNodes { get; set; }

    /// <summary>
    /// Number of DHT node cache entries
    /// </summary>
    public int DhtNodeCache { get; set; }

    /// <summary>
    /// Number of active DHT torrents
    /// </summary>
    public int DhtTorrents { get; set; }

    /// <summary>
    /// Total DHT bytes sent
    /// </summary>
    public long DhtBytesSent { get; set; }

    /// <summary>
    /// Total DHT bytes received
    /// </summary>
    public long DhtBytesReceived { get; set; }

    #endregion

    #region Tracker Statistics

    /// <summary>
    /// Number of tracker HTTP/UDP requests sent
    /// </summary>
    public int TrackerRequestsSent { get; set; }

    /// <summary>
    /// Number of successful tracker responses
    /// </summary>
    public int TrackerResponsesReceived { get; set; }

    /// <summary>
    /// Number of tracker errors
    /// </summary>
    public int TrackerErrors { get; set; }

    #endregion

    #region Disk Statistics

    /// <summary>
    /// Number of pending disk read operations
    /// </summary>
    public int DiskReadQueue { get; set; }

    /// <summary>
    /// Number of pending disk write operations
    /// </summary>
    public int DiskWriteQueue { get; set; }

    /// <summary>
    /// Total bytes read from disk
    /// </summary>
    public long DiskBytesRead { get; set; }

    /// <summary>
    /// Total bytes written to disk
    /// </summary>
    public long DiskBytesWritten { get; set; }

    /// <summary>
    /// Number of disk read operations
    /// </summary>
    public int DiskReadCount { get; set; }

    /// <summary>
    /// Number of disk write operations
    /// </summary>
    public int DiskWriteCount { get; set; }

    /// <summary>
    /// Current disk cache size in bytes
    /// </summary>
    public long DiskCacheSize { get; set; }

    /// <summary>
    /// Number of disk cache hits (read from cache)
    /// </summary>
    public int DiskCacheHits { get; set; }

    /// <summary>
    /// Number of disk cache misses (read from disk)
    /// </summary>
    public int DiskCacheMisses { get; set; }

    /// <summary>
    /// Disk cache hit ratio
    /// </summary>
    public float DiskCacheHitRatio =>
        (DiskCacheHits + DiskCacheMisses) > 0
            ? (float)DiskCacheHits / (DiskCacheHits + DiskCacheMisses)
            : 0f;

    #endregion

    #region Piece Statistics

    /// <summary>
    /// Total pieces passed hash check
    /// </summary>
    public int PiecesPassed { get; set; }

    /// <summary>
    /// Total pieces failed hash check
    /// </summary>
    public int PiecesFailed { get; set; }

    /// <summary>
    /// Piece hash check success rate
    /// </summary>
    public float PiecePassRate =>
        (PiecesPassed + PiecesFailed) > 0
            ? (float)PiecesPassed / (PiecesPassed + PiecesFailed)
            : 1f;

    #endregion

    #region Session Info

    /// <summary>
    /// When the session was started
    /// </summary>
    public DateTime SessionStartTime { get; set; }

    /// <summary>
    /// Session uptime
    /// </summary>
    public TimeSpan Uptime => DateTime.UtcNow - SessionStartTime;

    /// <summary>
    /// Whether the session is paused
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Listen port for incoming connections
    /// </summary>
    public int ListenPort { get; set; }

    /// <summary>
    /// External IP address (if determined)
    /// </summary>
    public string? ExternalIpAddress { get; set; }

    #endregion

    #region Methods

    /// <summary>
    /// Reset all session statistics
    /// </summary>
    public void Reset()
    {
        TotalBytesSent = 0;
        TotalBytesReceived = 0;

        GlobalDownloadRate = 0;
        GlobalUploadRate = 0;

        DownloadingTorrents = 0;
        SeedingTorrents = 0;
        PausedTorrents = 0;
        CheckingTorrents = 0;
        ErrorTorrents = 0;
        UploadOnlyTorrents = 0;

        TotalPeersConnected = 0;
        TotalConnectedSeeds = 0;
        HalfOpenConnections = 0;
        UploadingPeers = 0;
        DownloadingPeers = 0;
        UnchokedPeers = 0;
        ConnectionAttempts = 0;
        ConnectionsRejected = 0;

        DhtNodes = 0;
        DhtNodeCache = 0;
        DhtTorrents = 0;
        DhtBytesSent = 0;
        DhtBytesReceived = 0;

        TrackerRequestsSent = 0;
        TrackerResponsesReceived = 0;
        TrackerErrors = 0;

        DiskReadQueue = 0;
        DiskWriteQueue = 0;
        DiskBytesRead = 0;
        DiskBytesWritten = 0;
        DiskReadCount = 0;
        DiskWriteCount = 0;
        DiskCacheSize = 0;
        DiskCacheHits = 0;
        DiskCacheMisses = 0;

        PiecesPassed = 0;
        PiecesFailed = 0;

        SessionStartTime = DateTime.UtcNow;
        IsPaused = false;
    }

    /// <summary>
    /// Create a snapshot of current session statistics
    /// </summary>
    public SessionStatistics CreateSnapshot()
    {
        return new SessionStatistics
        {
            TotalBytesSent = this.TotalBytesSent,
            TotalBytesReceived = this.TotalBytesReceived,

            GlobalDownloadRate = this.GlobalDownloadRate,
            GlobalUploadRate = this.GlobalUploadRate,

            DownloadingTorrents = this.DownloadingTorrents,
            SeedingTorrents = this.SeedingTorrents,
            PausedTorrents = this.PausedTorrents,
            CheckingTorrents = this.CheckingTorrents,
            ErrorTorrents = this.ErrorTorrents,
            UploadOnlyTorrents = this.UploadOnlyTorrents,

            TotalPeersConnected = this.TotalPeersConnected,
            TotalConnectedSeeds = this.TotalConnectedSeeds,
            HalfOpenConnections = this.HalfOpenConnections,
            UploadingPeers = this.UploadingPeers,
            DownloadingPeers = this.DownloadingPeers,
            UnchokedPeers = this.UnchokedPeers,
            ConnectionAttempts = this.ConnectionAttempts,
            ConnectionsRejected = this.ConnectionsRejected,

            DhtNodes = this.DhtNodes,
            DhtNodeCache = this.DhtNodeCache,
            DhtTorrents = this.DhtTorrents,
            DhtBytesSent = this.DhtBytesSent,
            DhtBytesReceived = this.DhtBytesReceived,

            TrackerRequestsSent = this.TrackerRequestsSent,
            TrackerResponsesReceived = this.TrackerResponsesReceived,
            TrackerErrors = this.TrackerErrors,

            DiskReadQueue = this.DiskReadQueue,
            DiskWriteQueue = this.DiskWriteQueue,
            DiskBytesRead = this.DiskBytesRead,
            DiskBytesWritten = this.DiskBytesWritten,
            DiskReadCount = this.DiskReadCount,
            DiskWriteCount = this.DiskWriteCount,
            DiskCacheSize = this.DiskCacheSize,
            DiskCacheHits = this.DiskCacheHits,
            DiskCacheMisses = this.DiskCacheMisses,

            PiecesPassed = this.PiecesPassed,
            PiecesFailed = this.PiecesFailed,

            SessionStartTime = this.SessionStartTime,
            IsPaused = this.IsPaused,
            ListenPort = this.ListenPort,
            ExternalIpAddress = this.ExternalIpAddress
        };
    }

    #endregion

    #region DTO Factory

    /// <summary>
    /// Creates an immutable SessionOverview DTO from current statistics.
    /// </summary>
    public SessionOverview CreateOverview() => new SessionOverview
    {
        GlobalDownloadRate = GlobalDownloadRate,
        GlobalUploadRate = GlobalUploadRate,
        SessionDownloaded = TotalBytesReceived,
        SessionUploaded = TotalBytesSent,
        AllTimeDownloaded = TotalBytesReceived,
        AllTimeUploaded = TotalBytesSent,
        TotalTorrents = TotalTorrents,
        ActiveDownloads = DownloadingTorrents,
        ActiveUploads = SeedingTorrents,
        PausedTorrents = PausedTorrents,
        CheckingTorrents = CheckingTorrents,
        QueuedTorrents = 0,
        ErrorTorrents = ErrorTorrents,
        ConnectedPeers = TotalPeersConnected,
        TotalConnections = TotalPeersConnected + HalfOpenConnections,
        HalfOpenConnections = HalfOpenConnections,
        DhtNodes = DhtNodes,
        DhtEnabled = DhtNodes > 0,
        DiskReadQueue = DiskReadQueue,
        DiskWriteQueue = DiskWriteQueue,
        DiskBytesRead = DiskBytesRead,
        DiskBytesWritten = DiskBytesWritten,
        ListenPort = ListenPort,
        PortOpen = ListenPort > 0,
        ExternalIp = ExternalIpAddress,
        DownloadLimit = 0,
        UploadLimit = 0,
        IsPaused = IsPaused,
        Uptime = Uptime,
        FreeSpace = 0,
    };

    #endregion
}
