using System.Security.Cryptography;

namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Consolidated peer settings. Replaces PeerConnectionSettings and Core PeerSettings.
/// Single source of truth for all peer configuration.
/// </summary>
public class PeerSettings
{
    // ============================================
    // Connection settings
    // ============================================

    /// <summary>
    /// Maximum number of concurrent peer connections per torrent.
    /// Default: 200
    /// </summary>
    public int MaxConnections { get; set; } = 200;

    /// <summary>
    /// Maximum number of upload slots (unchoked peers) per torrent.
    /// Default: 4
    /// </summary>
    public int MaxUploadsPerTorrent { get; set; } = 4;

    /// <summary>
    /// TCP connection timeout (seconds). libtorrent: 15.
    /// </summary>
    public int ConnectTimeout { get; set; } = 15;

    /// <summary>
    /// Handshake timeout (seconds)
    /// </summary>
    public int HandshakeTimeout { get; set; } = 10;

    /// <summary>
    /// uTP connection timeout in milliseconds
    /// </summary>
    public int UtpConnectTimeoutMs { get; set; } = 5_000;

    /// <summary>
    /// Port to listen for incoming peer connections.
    /// </summary>
    public int ListenPort { get; set; } = 6881;

    // ============================================
    // Timeout settings
    // ============================================

    /// <summary>
    /// Block request timeout (seconds)
    /// </summary>
    public int RequestTimeout { get; set; } = 60;

    /// <summary>
    /// Piece completion timeout (seconds)
    /// </summary>
    public int PieceTimeout { get; set; } = 20;

    /// <summary>
    /// Peer inactivity timeout (seconds)
    /// </summary>
    public int InactivityTimeout { get; set; } = 600;

    // ============================================
    // Unchoke settings
    // ============================================

    /// <summary>
    /// Interval between unchoke rounds (seconds)
    /// </summary>
    public int UnchokeInterval { get; set; } = 15;

    /// <summary>
    /// Interval for optimistic unchoke (seconds)
    /// </summary>
    public int OptimisticUnchokeInterval { get; set; } = 30;

    // ============================================
    // Request pipeline
    // ============================================

    /// <summary>
    /// Maximum pending block requests per peer
    /// </summary>
    public int MaxPendingBlocksPerPeer { get; set; } = 500;

    // ============================================
    // Send buffer flow control
    // ============================================

    /// <summary>
    /// Maximum send buffer size in bytes across all peers for this torrent.
    /// 0 = auto-tune based on aggregate upload throughput (recommended).
    /// When set manually, acts as a hard ceiling for per-peer watermarks.
    /// </summary>
    public int SendBufferWatermark { get; set; } = 0;

    /// <summary>
    /// Minimum send buffer target per peer in bytes. Ensures at least this many
    /// bytes are buffered even for slow peers. Should be >= 16384 (one block).
    /// </summary>
    public int SendBufferLowWatermark { get; set; } = 10 * 1024;

    /// <summary>
    /// Multiplier for per-peer send buffer sizing, as a percentage of the peer's
    /// current upload rate (bytes/sec). Higher values buffer more aggressively.
    /// Example: 50 means buffer = upload_rate * 0.5 seconds of data.
    /// </summary>
    public int SendBufferWatermarkFactor { get; set; } = 50;

    // ============================================
    // Identity
    // ============================================

    /// <summary>
    /// Unique peer ID for this client session (20 bytes, ASCII).
    /// Auto-generated following BitTorrent conventions: -VT0100-############
    /// </summary>
    public string PeerId { get; set; } = GeneratePeerId();

    /// <summary>
    /// Client version string for extension handshakes.
    /// </summary>
    public string ClientVersion { get; set; } = "vTorrent/1.0";

    // ============================================
    // Protocol features
    // ============================================

    /// <summary>
    /// Enable Peer Exchange (PEX) for peer discovery.
    /// </summary>
    public bool EnablePex { get; set; } = true;

    // ============================================
    // Behavior flags (wired from BehaviorSettings)
    // ============================================

    /// <summary>
    /// Finish partially downloaded pieces before starting new ones.
    /// </summary>
    public bool PrioritizePartialPieces { get; set; } = false;

    /// <summary>
    /// Limit duplicate block requests in endgame to 1 per peer.
    /// </summary>
    public bool StrictEndgameMode { get; set; } = true;

    /// <summary>
    /// Close seed-to-seed connections.
    /// </summary>
    public bool CloseRedundantConnections { get; set; } = true;

    /// <summary>
    /// Make outgoing connections while seeding.
    /// </summary>
    public bool SeedingOutgoingConnections { get; set; } = true;

    // ============================================
    // Choking tuning
    // ============================================

    /// <summary>Number of optimistic unchoke slots. 0 = auto (20% of upload slots). libtorrent default: 0</summary>
    public int NumOptimisticUnchokeSlots { get; set; } = 0;

    // ============================================
    // uTP tuning (LEDBAT congestion control)
    // ============================================

    /// <summary>LEDBAT target delay in milliseconds. libtorrent default: 100</summary>
    public int UtpTargetDelay { get; set; } = 100;

    /// <summary>Max congestion window increase per RTT. libtorrent default: 3000</summary>
    public int UtpGainFactor { get; set; } = 3000;

    /// <summary>Minimum retransmission timeout in milliseconds. libtorrent default: 500</summary>
    public int UtpMinTimeout { get; set; } = 500;

    /// <summary>SYN retransmission count before giving up. libtorrent default: 2</summary>
    public int UtpSynResends { get; set; } = 2;

    /// <summary>FIN retransmission count. libtorrent default: 2</summary>
    public int UtpFinResends { get; set; } = 2;

    /// <summary>Data retransmission count before connection is considered lost. libtorrent default: 3</summary>
    public int UtpNumResends { get; set; } = 3;

    /// <summary>Window reduction on loss as percentage (50 = halve window). libtorrent default: 50</summary>
    public int UtpLossMultiplier { get; set; } = 50;

    /// <summary>Milliseconds between congestion window reductions. libtorrent default: 100</summary>
    public int UtpCwndReduceTimer { get; set; } = 100;

    // ============================================
    // Disk
    // ============================================

    /// <summary>
    /// Disk write cache size in bytes.
    /// Default: 64 MB
    /// </summary>
    public long DiskCacheSize { get; set; } = 64 * 1024 * 1024;

    // ============================================
    // Socket / protocol tuning
    // ============================================

    /// <summary>
    /// Raw DSCP value (0-63) set on peer sockets. Left-shifted by 2 to produce ToS byte for setsockopt.
    /// libtorrent: peer_dscp. Default: 0x04 (DSCP 4 → ToS 0x10).
    /// </summary>
    public int PeerDscp { get; set; } = 0x04;

    /// <summary>Number of pieces in the BEP 6 Allowed Fast Set. libtorrent: allowed_fast_set_size. Default: 5.</summary>
    public int AllowedFastSetSize { get; set; } = 5;

    /// <summary>Consecutive rejected requests before disconnecting/banning a peer. libtorrent: max_rejects. Default: 50.</summary>
    public int MaxRejects { get; set; } = 50;

    /// <summary>Seconds of download to keep queued in the request pipeline. libtorrent: request_queue_time. Default: 3.</summary>
    public int RequestQueueTime { get; set; } = 3;

    /// <summary>
    /// Maximum metadata size accepted from magnet links (bytes).
    /// libtorrent: max_metadata_size. Default: 31457280 (30 MB).
    /// Breaking change: bumped from 10 MB to match libtorrent — real-world torrents with many files can exceed 10 MB.
    /// </summary>
    public int MaxMetadataSize { get; set; } = 31457280;

    /// <summary>Maximum known peers per torrent. Oldest/lowest-priority peers evicted when full. libtorrent: max_peerlist_size. Default: 3000.</summary>
    public int MaxPeerlistSize { get; set; } = 3000;

    // ============================================
    // Helpers
    // ============================================

    private static string GeneratePeerId()
    {
        const string prefix = "-VT0100-";
        Span<byte> random = stackalloc byte[12];
        RandomNumberGenerator.Fill(random);
        var chars = new char[20];
        prefix.AsSpan().CopyTo(chars);
        for (int i = 0; i < 12; i++)
            chars[8 + i] = (char)('0' + random[i] % 10);
        return new string(chars);
    }
}
