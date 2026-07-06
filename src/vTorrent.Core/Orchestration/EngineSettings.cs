namespace vTorrent.Core.Orchestration;

/// <summary>
/// Per-torrent engine settings applied when creating a TorrentEngine
/// </summary>
public class EngineSettings
{
    /// <summary>
    /// Maximum connections for this torrent (0 = use global default)
    /// </summary>
    public int MaxConnections { get; set; } = 0;

    /// <summary>
    /// Download rate limit in bytes/sec (0 = unlimited)
    /// </summary>
    public int DownloadLimit { get; set; } = 0;

    /// <summary>
    /// Upload rate limit in bytes/sec (0 = unlimited)
    /// </summary>
    public int UploadLimit { get; set; } = 0;

    /// <summary>
    /// Whether to enable sequential download mode
    /// </summary>
    public bool SequentialDownload { get; set; } = false;

    /// <summary>
    /// Priority (affects piece selection and bandwidth allocation)
    /// </summary>
    public TorrentPriority Priority { get; set; } = TorrentPriority.Normal;

    /// <summary>
    /// Create default settings
    /// </summary>
    public static EngineSettings Default => new();

    /// <summary>
    /// Create from ManagedTorrent state
    /// </summary>
    public static EngineSettings FromManagedTorrent(ManagedTorrent managed)
    {
        return new EngineSettings
        {
            SequentialDownload = managed.SequentialDownload
        };
    }
}

/// <summary>
/// Torrent priority levels
/// </summary>
public enum TorrentPriority
{
    Low = -1,
    Normal = 0,
    High = 1
}
