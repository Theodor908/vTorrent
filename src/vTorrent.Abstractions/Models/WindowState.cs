using System;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// Represents the persisted window state including position, size, and layout.
/// Restored on application startup to provide consistent user experience.
/// </summary>
public class PersistedWindowState
{
    /// <summary>
    /// Window X position (left edge)
    /// </summary>
    public int X { get; set; }

    /// <summary>
    /// Window Y position (top edge)
    /// </summary>
    public int Y { get; set; }

    /// <summary>
    /// Window width
    /// </summary>
    public int Width { get; set; } = 1280;

    /// <summary>
    /// Window height
    /// </summary>
    public int Height { get; set; } = 720;

    /// <summary>
    /// Whether window is maximized
    /// </summary>
    public bool IsMaximized { get; set; }

    /// <summary>
    /// Sidebar panel width
    /// </summary>
    public double SidebarWidth { get; set; } = 200;

    /// <summary>
    /// Details panel height (bottom panel)
    /// </summary>
    public double DetailsHeight { get; set; } = 200;

    /// <summary>
    /// Schema version for future migrations
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Check if the position is valid (on screen)
    /// </summary>
    public bool HasValidPosition()
    {
        // Basic sanity check - position should be reasonable
        return X >= -10000 && X <= 10000 && Y >= -10000 && Y <= 10000;
    }

    /// <summary>
    /// Check if the size is valid
    /// </summary>
    public bool HasValidSize()
    {
        return Width >= 400 && Width <= 10000 && Height >= 300 && Height <= 10000;
    }
}
