namespace vTorrent.Abstractions.Enums;

/// <summary>
/// Priority levels for selective file download.
/// Follows libtorrent's file priority model.
/// </summary>
public enum FilePriority
{
    /// <summary>
    /// File will not be downloaded (skipped)
    /// </summary>
    Skip = 0,

    /// <summary>
    /// Lowest priority - downloaded last
    /// </summary>
    Lowest = 1,

    /// <summary>
    /// Low priority
    /// </summary>
    Low = 2,

    /// <summary>
    /// Below normal priority
    /// </summary>
    BelowNormal = 3,

    /// <summary>
    /// Normal priority (default)
    /// </summary>
    Normal = 4,

    /// <summary>
    /// Above normal priority
    /// </summary>
    AboveNormal = 5,

    /// <summary>
    /// High priority
    /// </summary>
    High = 6,

    /// <summary>
    /// Highest priority - downloaded first
    /// </summary>
    Highest = 7
}

/// <summary>
/// Extension methods for FilePriority
/// </summary>
public static class FilePriorityExtensions
{
    /// <summary>
    /// Convert priority to display string
    /// </summary>
    public static string ToDisplayString(this FilePriority priority)
    {
        return priority switch
        {
            FilePriority.Skip => "Skip",
            FilePriority.Lowest => "Lowest",
            FilePriority.Low => "Low",
            FilePriority.BelowNormal => "Below Normal",
            FilePriority.Normal => "Normal",
            FilePriority.AboveNormal => "Above Normal",
            FilePriority.High => "High",
            FilePriority.Highest => "Highest",
            _ => priority.ToString()
        };
    }

    /// <summary>
    /// Check if the file should be downloaded
    /// </summary>
    public static bool IsWanted(this FilePriority priority)
    {
        return priority != FilePriority.Skip;
    }
}
