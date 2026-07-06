using System.Text.Json.Serialization;

namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Download/seed queue settings
/// </summary>
public class QueueSettings
{
    /// <summary>
    /// Maximum simultaneously active downloads
    /// </summary>
    public int MaxActiveDownloads { get; set; } = 3;

    /// <summary>
    /// Maximum simultaneously active seeds (-1 = unlimited)
    /// </summary>
    public int MaxActiveSeeds { get; set; } = 5;

    /// <summary>
    /// Maximum total active torrents
    /// </summary>
    public int MaxActiveTorrents { get; set; } = 10;

    /// <summary>
    /// Download rate threshold for slow torrent (bytes/s)
    /// libtorrent: inactive_down_rate (default 2048)
    /// </summary>
    [JsonPropertyName("SlowTorrentDownloadThreshold")]
    public int InactiveDownRate { get; set; } = 2048;

    /// <summary>
    /// Upload rate threshold for slow torrent (bytes/s)
    /// libtorrent: inactive_up_rate (default 2048)
    /// </summary>
    [JsonPropertyName("SlowTorrentUploadThreshold")]
    public int InactiveUpRate { get; set; } = 2048;

    /// <summary>
    /// When true, inactive (slow) torrents bypass per-type slot limits
    /// (MaxActiveDownloads/MaxActiveSeeds) but still count against MaxActiveTorrents.
    /// libtorrent: dont_count_slow_torrents (default true)
    /// </summary>
    public bool DontCountSlowTorrents { get; set; } = true;

    /// <summary>Seconds between auto-manage recalculations. libtorrent default: 30</summary>
    public int AutoManageInterval { get; set; } = 30;

    /// <summary>
    /// Grace period in seconds before auto-manage activates after session start.
    /// libtorrent default: 60 (for headless daemons with 1000s of torrents).
    /// Reduced to 5s for desktop use — DHT bootstraps in parallel, and cached resume data
    /// means engines start quickly without needing DHT peers first.
    /// </summary>
    public int AutoManageStartup { get; set; } = 5;

    /// <summary>Connect to 1 seed for every N download connections. libtorrent default: 10</summary>
    public int ConnectSeedEveryNDownload { get; set; } = 10;

    /// <summary>
    /// Maximum number of torrent engines starting simultaneously at boot.
    /// Higher values speed up startup but increase disk/CPU pressure.
    /// Default 8 balances startup speed with I/O contention.
    /// </summary>
    public int EngineStartConcurrency { get; set; } = 8;
}
