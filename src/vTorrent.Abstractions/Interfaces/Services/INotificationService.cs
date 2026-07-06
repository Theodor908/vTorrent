using System;
using System.Collections.Generic;

namespace vTorrent.Abstractions.Interfaces.Services;

/// <summary>
/// Service interface for managing system notifications.
/// Handles OS-level toast notifications for torrent events.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Whether notifications are enabled
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>
    /// Show a notification when a torrent download completes
    /// </summary>
    bool NotifyOnDownloadComplete { get; set; }

    /// <summary>
    /// Show a notification when a torrent download fails
    /// </summary>
    bool NotifyOnDownloadFailed { get; set; }

    /// <summary>
    /// Show a notification when a torrent is added
    /// </summary>
    bool NotifyOnTorrentAdded { get; set; }

    /// <summary>
    /// Play sound with notifications
    /// </summary>
    bool PlaySound { get; set; }

    /// <summary>
    /// Shows a notification with the given title and message
    /// </summary>
    /// <param name="title">Notification title</param>
    /// <param name="message">Notification message body</param>
    /// <param name="type">Type of notification for icon/styling</param>
    void Show(string title, string message, NotificationType type = NotificationType.Info);

    /// <summary>
    /// Notify that a torrent download has completed
    /// </summary>
    /// <param name="torrentName">Name of the completed torrent</param>
    void NotifyDownloadComplete(string torrentName);

    /// <summary>
    /// Notify that a torrent download has failed
    /// </summary>
    /// <param name="torrentName">Name of the failed torrent</param>
    /// <param name="error">Error message</param>
    void NotifyDownloadFailed(string torrentName, string? error = null);

    /// <summary>
    /// Notify that a torrent has been added
    /// </summary>
    /// <param name="torrentName">Name of the added torrent</param>
    void NotifyTorrentAdded(string torrentName);

    /// <summary>
    /// Notify that a seeding limit has been reached
    /// </summary>
    /// <param name="torrentName">Name of the torrent</param>
    /// <param name="limitType">Type of limit reached (ratio or time)</param>
    /// <param name="action">Action taken (pause or remove)</param>
    void NotifySeedingLimitReached(string torrentName, string limitType, string action);

    /// <summary>
    /// Shows a debug notification to test the notification system
    /// </summary>
    void ShowDebugNotification();

    /// <summary>
    /// Raised when notification settings change
    /// </summary>
    event EventHandler<bool>? SettingsChanged;

    /// <summary>
    /// Raised when an in-app toast should be shown
    /// </summary>
    event EventHandler<InAppNotificationEventArgs>? InAppNotificationRequested;

    /// <summary>
    /// Returns the history of recent notifications (newest first).
    /// </summary>
    IReadOnlyList<NotificationHistoryItem> GetHistory();
}

/// <summary>
/// Type of notification for styling purposes
/// </summary>
public enum NotificationType
{
    /// <summary>Informational notification</summary>
    Info,

    /// <summary>Success notification</summary>
    Success,

    /// <summary>Warning notification</summary>
    Warning,

    /// <summary>Error notification</summary>
    Error
}

public class InAppNotificationEventArgs : EventArgs
{
    public string Title { get; }
    public string Message { get; }
    public NotificationType Type { get; }

    public InAppNotificationEventArgs(string title, string message, NotificationType type)
    {
        Title = title;
        Message = message;
        Type = type;
    }
}

public record NotificationHistoryItem(string Title, string Message, NotificationType Type, DateTime Timestamp);
