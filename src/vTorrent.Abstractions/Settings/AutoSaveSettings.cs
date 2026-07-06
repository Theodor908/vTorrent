namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Auto-save settings
/// </summary>
public class AutoSaveSettings
{
    /// <summary>
    /// Enable automatic saving of resume data
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Auto-save interval in minutes
    /// </summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Save resume data when torrent completes
    /// </summary>
    public bool SaveOnTorrentComplete { get; set; } = true;

    /// <summary>
    /// Save resume data when torrent is paused
    /// </summary>
    public bool SaveOnPause { get; set; } = true;

    /// <summary>
    /// Save resume data when torrent is resumed (prevents state loss on crash after resume)
    /// </summary>
    public bool SaveOnResume { get; set; } = true;
}
