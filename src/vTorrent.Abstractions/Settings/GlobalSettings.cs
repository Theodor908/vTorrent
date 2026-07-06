using System;
using System.Text.Json.Serialization;

namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Global application settings for vTorrent.
/// Follows libtorrent's settings_pack model with JSON persistence.
/// </summary>
public class GlobalSettings
{
    /// <summary>
    /// Current version of the settings schema
    /// </summary>
    public const int CurrentVersion = 11;

    /// <summary>
    /// Settings schema version for migration support
    /// </summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>
    /// When settings were last modified
    /// </summary>
    public DateTime UpdatedOn { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Connection-related settings
    /// </summary>
    public ConnectionSettings Connection { get; set; } = new();

    /// <summary>
    /// Bandwidth and rate limiting settings
    /// </summary>
    public BandwidthSettings Bandwidth { get; set; } = new();

    /// <summary>
    /// Protocol feature settings (DHT, PEX, encryption)
    /// </summary>
    public ProtocolSettings Protocol { get; set; } = new();

    /// <summary>
    /// DHT (Distributed Hash Table) settings
    /// </summary>
    public DhtSettings Dht { get; set; } = new();

    /// <summary>
    /// Disk I/O and storage settings
    /// </summary>
    public DiskSettings Disk { get; set; } = new();

    /// <summary>
    /// Download/seed queue management settings
    /// </summary>
    public QueueSettings Queue { get; set; } = new();

    /// <summary>
    /// General behavior settings
    /// </summary>
    public BehaviorSettings Behavior { get; set; } = new();

    /// <summary>
    /// Tracker communication settings
    /// </summary>
    public TrackerSettings Tracker { get; set; } = new();

    /// <summary>
    /// Peer connection settings
    /// </summary>
    public PeerSettings Peer { get; set; } = new();

    /// <summary>
    /// Auto-save and persistence settings
    /// </summary>
    public AutoSaveSettings AutoSave { get; set; } = new();

    /// <summary>
    /// Logging configuration
    /// </summary>
    public LoggingSettings Logging { get; set; } = new();

    /// <summary>
    /// Encryption settings (MSE/PE)
    /// </summary>
    public EncryptionSettings Encryption { get; set; } = new();

    /// <summary>
    /// User interface settings
    /// </summary>
    public UISettings UI { get; set; } = new();

    /// <summary>
    /// Web seed (BEP 17/19) settings
    /// </summary>
    public WebSeedSettings WebSeed { get; set; } = new();

    /// <summary>
    /// Privacy and secure deletion settings
    /// </summary>
    public PrivacySettings Privacy { get; set; } = new();

    /// <summary>Proxy settings (SOCKS4/5, HTTP CONNECT).</summary>
    public ProxySettings Proxy { get; set; } = new();

    /// <summary>VPN kill-switch settings.</summary>
    public VpnSettings Vpn { get; set; } = new();

    /// <summary>I2P (SAM) anonymity layer settings.</summary>
    public I2pSettings I2p { get; set; } = new();

    /// <summary>Peer class IP-based bandwidth shaping settings.</summary>
    public PeerClassSettings PeerClasses { get; set; } = new();

    /// <summary>Web server and remote access settings.</summary>
    public ServerSettings Server { get; set; } = new();

    /// <summary>Name of the currently active performance profile.</summary>
    public string ActiveProfileName { get; set; } = "Balanced";

    /// <summary>Accent color of the currently active profile (hex).</summary>
    public string ActiveProfileColor { get; set; } = "#2196F3";

    /// <summary>Weekly profile scheduler settings.</summary>
    public ScheduleSettings Schedule { get; set; } = new();
}

/// <summary>
/// Web seed (BEP 17/19) settings — mirrors libtorrent's urlseed_* settings.
/// </summary>
public class WebSeedSettings
{
    /// <summary>Max concurrent web seed connections per torrent (libtorrent: max_web_seed_connections = 3).</summary>
    public int MaxConnectionsPerTorrent { get; set; } = 3;

    /// <summary>HTTP request timeout in seconds (libtorrent: urlseed_timeout = 20).</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Seconds before retrying a failed web seed (libtorrent: urlseed_wait_retry = 30).</summary>
    public int WaitRetrySeconds { get; set; } = 30;

    /// <summary>Max bytes per HTTP Range request (libtorrent: urlseed_max_request_bytes = 16 MB).</summary>
    public int MaxRequestBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Include User-Agent header in every web seed request. libtorrent default: false.</summary>
    public bool AlwaysSendUserAgent { get; set; } = false;

    /// <summary>Ban web seeds that send corrupt data (hash mismatch). libtorrent: ban_web_seeds. Default: true.</summary>
    public bool BanWebSeeds { get; set; } = true;
}

/// <summary>
/// Privacy and secure deletion settings.
/// </summary>
public class PrivacySettings
{
    /// <summary>
    /// When true, file deletion overwrites bytes with random data before removing.
    /// Effective on HDDs; best-effort on SSDs due to wear leveling.
    /// </summary>
    public bool SecureDeletion { get; set; } = false;

    /// <summary>
    /// When true, secure deletion also applies to .torrent, .resume, and per-torrent settings files.
    /// </summary>
    public bool SecureDeletionIncludeMetadata { get; set; } = false;

    /// <summary>
    /// Hides client identity: empty user-agent for trackers, empty client version
    /// in BEP 10 extension handshake, suppress announce_ip. Does NOT affect
    /// peer ID prefix, DHT, or proxy settings (those have their own toggles).
    /// </summary>
    public bool AnonymousMode { get; set; } = false;
}
