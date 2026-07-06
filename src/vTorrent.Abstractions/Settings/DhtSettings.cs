using System;

namespace vTorrent.Abstractions.Settings;

/// <summary>
/// DHT (Distributed Hash Table) settings.
/// Consolidated single source of truth — replaces DhtGlobalSettings.
/// </summary>
public class DhtSettings
{
    /// <summary>
    /// Enable DHT peer discovery
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// UDP port for DHT operations
    /// </summary>
    public int Port { get; set; } = 6881;

    /// <summary>
    /// Number of concurrent requests during lookups
    /// </summary>
    public int SearchBranching { get; set; } = 5;

    /// <summary>
    /// Query timeout in milliseconds
    /// </summary>
    public int QueryTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Maximum peers to return in get_peers
    /// </summary>
    public int MaxPeersReply { get; set; } = 100;

    /// <summary>
    /// Maximum peers per info_hash to store
    /// </summary>
    public int MaxPeersPerInfoHash { get; set; } = 500;

    /// <summary>
    /// Bootstrap nodes for initial DHT entry
    /// </summary>
    public string[] BootstrapNodes { get; set; } =
    {
        "dht.libtorrent.org:25401",
        "dht.transmissionbt.com:6881",
        "router.bittorrent.com:6881",
        "router.utorrent.com:6881"
    };

    /// <summary>
    /// Bootstrap nodes for I2P DHT network (base32 .i2p addresses with port).
    /// </summary>
    public string[] I2pBootstrapNodes { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Enforce node ID verification (BEP 42)
    /// </summary>
    public bool EnforceNodeId { get; set; } = true;

    /// <summary>
    /// Restrict to one entry per IP in routing table
    /// </summary>
    public bool RestrictRoutingIps { get; set; } = true;

    /// <summary>
    /// Use extended routing table with larger buckets
    /// </summary>
    public bool ExtendedRoutingTable { get; set; } = true;

    /// <summary>
    /// Announce interval in milliseconds
    /// </summary>
    public int AnnounceIntervalMs { get; set; } = 900_000; // 15 minutes (libtorrent default)

    /// <summary>
    /// Enable DoS blocker for DHT rate limiting
    /// </summary>
    public bool EnableDosBlocker { get; set; } = true;

    /// <summary>
    /// BEP 43: Operate as read-only DHT node.
    /// Read-only nodes participate in lookups but don't respond to queries
    /// or get added to other nodes' routing tables.
    /// Useful for low-resource mode or privacy-conscious users.
    /// </summary>
    public bool ReadOnly { get; set; } = false;

    /// <summary>
    /// BEP 51: Maximum number of infohashes to include in a sample_infohashes response.
    /// Default: 20 (per BEP 51 recommendation)
    /// </summary>
    public int MaxSampleCount { get; set; } = 20;

    /// <summary>
    /// BEP 51: Interval in seconds between sample cache refreshes.
    /// Default: 600 (10 minutes)
    /// </summary>
    public int SampleInfohashesIntervalSeconds { get; set; } = 600;

    // ============================================
    // Promoted from Core DhtSettings
    // ============================================

    /// <summary>
    /// Maximum number of failed queries before a node is considered bad.
    /// </summary>
    public int MaxFailCount { get; set; } = 5;

    /// <summary>
    /// Maximum total number of info_hashes to store peer data for.
    /// </summary>
    public int MaxInfoHashes { get; set; } = 2000;

    /// <summary>
    /// Maximum number of stored peers across all info_hashes.
    /// </summary>
    public int MaxTotalPeers { get; set; } = 100_000;

    /// <summary>
    /// Block IPs that exceed rate limits for this duration in seconds.
    /// </summary>
    public int BlockTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum upload rate for DHT in bytes/second.
    /// </summary>
    public int UploadRateLimitBytesPerSec { get; set; } = 8000;

    /// <summary>
    /// Rate limit for incoming packets per IP (packets per second).
    /// </summary>
    public int BlockRateLimitPacketsPerSec { get; set; } = 5;

    /// <summary>
    /// Maximum number of abusive IPs to track for rate limiting.
    /// </summary>
    public int MaxBlockedIps { get; set; } = 20;

    /// <summary>
    /// Prefer verified node IDs when splitting buckets.
    /// </summary>
    public bool PreferVerifiedNodeIds { get; set; } = true;
}
