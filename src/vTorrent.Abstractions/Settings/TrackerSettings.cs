namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Tracker communication settings.
/// Consolidated single source of truth for all tracker configuration.
/// </summary>
public class TrackerSettings
{
    /// <summary>
    /// Announce to all trackers (not just first working)
    /// </summary>
    public bool AnnounceToAllTrackers { get; set; } = false;

    /// <summary>
    /// Announce to all tiers (not just first tier)
    /// </summary>
    public bool AnnounceToAllTiers { get; set; } = false;

    /// <summary>
    /// Timeout for tracker stop event on shutdown (seconds)
    /// </summary>
    public int StopTrackerTimeout { get; set; } = 5;

    /// <summary>
    /// Number of peers to request from tracker
    /// </summary>
    public int NumWant { get; set; } = 200;

    /// <summary>
    /// HTTP tracker timeout (seconds)
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// UDP tracker timeout (seconds)
    /// </summary>
    public int UdpTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Maximum tracker retry attempts
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    // ============================================
    // Promoted from Core TrackerSettings
    // ============================================

    /// <summary>
    /// Delay between tracker retry attempts (seconds)
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Minimum announce interval in seconds
    /// </summary>
    public int MinAnnounceInterval { get; set; } = 300;

    /// <summary>
    /// Auto-scrape interval in seconds. How often to scrape trackers for seeder/leecher counts.
    /// libtorrent default: 1800 (30 minutes).
    /// </summary>
    public int AutoScrapeInterval { get; set; } = 1800;

    /// <summary>
    /// Maximum concurrent announce requests
    /// </summary>
    public int MaxConcurrentAnnounces { get; set; } = 10;

    /// <summary>
    /// Port to report to trackers for incoming connections.
    /// </summary>
    public int ListenPort { get; set; } = 6881;

    /// <summary>
    /// User-Agent string for HTTP tracker requests.
    /// </summary>
    public string UserAgent { get; set; } = "vTorrent/1.0";

    /// <summary>
    /// Announce to all tracker tiers in parallel.
    /// </summary>
    public bool ParallelAnnounceAcrossTiers { get; set; } = true;

    /// <summary>
    /// Maximum number of concurrent tracker announces.
    /// </summary>
    public int MaxParallelAnnounces { get; set; } = 10;

    // ============================================
    // Behavior flags (wired from BehaviorSettings)
    // ============================================

    /// <summary>
    /// Include redundant byte count in tracker announce. libtorrent default: true.
    /// </summary>
    public bool ReportRedundantBytes { get; set; } = true;

    /// <summary>
    /// Include redundant bytes in the "downloaded" count reported to tracker. libtorrent default: false.
    /// </summary>
    public bool ReportTrueDownloaded { get; set; } = false;

    // ============================================
    // Parity flags (libtorrent feature parity)
    // ============================================

    /// <summary>Prefer UDP trackers over HTTP when both are available. libtorrent default: true.</summary>
    public bool PreferUdpTrackers { get; set; } = true;

    /// <summary>Include &amp;supportcrypt=1 in HTTP tracker announces. libtorrent default: true.</summary>
    public bool AnnounceCryptoSupport { get; set; } = true;

    /// <summary>Apply IP filter to tracker connections. libtorrent default: true.</summary>
    public bool ApplyIpFilterToTrackers { get; set; } = true;

    /// <summary>Custom IP to report to tracker (empty = don't include). libtorrent default: "".</summary>
    public string AnnounceIp { get; set; } = "";

    /// <summary>Validate HTTPS tracker certificates. libtorrent default: true.</summary>
    public bool ValidateHttpsTrackers { get; set; } = true;

    /// <summary>Restrict tracker/web seed requests to prevent SSRF attacks. libtorrent default: true.</summary>
    public bool SsrfMitigation { get; set; } = true;

    // ============================================
    // Announce timing (libtorrent parity)
    // ============================================

    /// <summary>
    /// Exponential backoff factor (percent) for tracker announce interval on consecutive failures.
    /// After N failures: effective_interval = base_interval * (TrackerBackoff / 100.0) ^ failcount.
    /// The base_interval is the tracker's last recommended interval (stored separately, not mutated by backoff).
    /// libtorrent default: 250 (2.5x per failure).
    /// Setting this to 100 disables backoff (factor = 1.0, interval unchanged after failures).
    /// </summary>
    public int TrackerBackoff { get; set; } = 250;

    /// <summary>
    /// Minimum seconds between auto-scrape requests. Prevents hammering tracker with scrapes.
    /// libtorrent default: 300 (5 minutes).
    /// </summary>
    public int AutoScrapeMinInterval { get; set; } = 300;
}
