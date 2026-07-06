namespace vTorrent.Abstractions.Settings;

/// <summary>
/// User interface settings
/// </summary>
public class UISettings
{
    /// <summary>
    /// Theme mode: "Dark", "Light", or "System"
    /// </summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>
    /// Enable system notifications
    /// </summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Show notification when torrent download completes
    /// </summary>
    public bool NotifyOnDownloadComplete { get; set; } = true;

    /// <summary>
    /// Show notification when torrent download fails
    /// </summary>
    public bool NotifyOnDownloadFailed { get; set; } = true;

    /// <summary>
    /// Show notification when torrent is added
    /// </summary>
    public bool NotifyOnTorrentAdded { get; set; } = false;

    /// <summary>
    /// Play sound with notifications
    /// </summary>
    public bool PlayNotificationSound { get; set; } = true;

    /// <summary>
    /// Hide to system tray when the window close button is clicked.
    /// When false, clicking close quits the application.
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// Hide to system tray when the window is minimized.
    /// When false, minimize goes to the taskbar normally.
    /// </summary>
    public bool MinimizeToTray { get; set; } = false;

    /// <summary>
    /// Start the application minimized to the system tray.
    /// </summary>
    public bool StartMinimizedToTray { get; set; } = false;
}
