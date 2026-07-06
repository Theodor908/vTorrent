namespace vTorrent.Abstractions.Enums;

/// <summary>
/// What does the user or auto-manager want this torrent to do?
/// </summary>
public enum UserIntent
{
    Active,                // Should be running
    Paused,                // User explicitly paused
    Queued                 // Auto-manager holding it back
}
