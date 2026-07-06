namespace vTorrent.Core.Events;

/// <summary>
/// Unified alert -- replaces specific alert class hierarchy with severity + category.
/// </summary>
public class AlertEventArgs : EventArgs
{
    public AlertSeverity Severity { get; init; }
    public string Category { get; init; } = "";       // "tracker", "disk", "peer", "torrent"
    public string Message { get; init; } = "";
    public string? InfoHash { get; init; }             // null for session-level alerts
}

/// <summary>
/// Alert severity levels for filtering and display.
/// </summary>
public enum AlertSeverity
{
    Debug,
    Info,
    Warning,
    Error,
    Critical
}
