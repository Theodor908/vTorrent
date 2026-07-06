using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Bandwidth and rate limiting settings
/// </summary>
public class BandwidthSettings
{
    /// <summary>
    /// Global download rate limit in bytes/s (0 = unlimited)
    /// </summary>
    public int GlobalDownloadLimit { get; set; } = 0;

    /// <summary>
    /// Global upload rate limit in bytes/s (0 = unlimited)
    /// </summary>
    public int GlobalUploadLimit { get; set; } = 0;

    /// <summary>
    /// Default per-torrent download limit in bytes/s (0 = unlimited)
    /// </summary>
    public int PerTorrentDownloadLimit { get; set; } = 0;

    /// <summary>
    /// Default per-torrent upload limit in bytes/s (0 = unlimited)
    /// </summary>
    public int PerTorrentUploadLimit { get; set; } = 0;

    /// <summary>
    /// Include IP overhead in rate limit calculations
    /// </summary>
    public bool RateLimitIpOverhead { get; set; } = false;

    /// <summary>TCP/uTP bandwidth sharing strategy. Default: PeerProportional (libtorrent default)</summary>
    public MixedModeAlgorithm MixedModeAlgorithm { get; set; } = MixedModeAlgorithm.PeerProportional;
}
