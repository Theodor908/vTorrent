using System;
using System.Collections.Generic;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// Represents the persisted view state including sort preferences, filters, and selections.
/// Restored on application startup to provide consistent user experience.
/// </summary>
public class ViewState
{
    /// <summary>
    /// Current sort column name
    /// </summary>
    public string SortColumn { get; set; } = "Name";

    /// <summary>
    /// Sort direction (true = ascending, false = descending)
    /// </summary>
    public bool SortAscending { get; set; } = true;

    /// <summary>
    /// Active navigation section (e.g., "All", "Downloading", "Seeding", "Completed")
    /// </summary>
    public string ActiveSection { get; set; } = "All";

    /// <summary>
    /// Last search query (optional - may not want to restore this)
    /// </summary>
    public string? SearchQuery { get; set; }

    /// <summary>
    /// Info hash of the last selected torrent (for restoring selection)
    /// </summary>
    public string? SelectedInfoHash { get; set; }

    /// <summary>
    /// Selected category ID (null for "All" categories)
    /// </summary>
    public int? SelectedCategoryId { get; set; }

    /// <summary>
    /// Selected tag ID (null for "All" tags)
    /// </summary>
    public int? SelectedTagId { get; set; }

    /// <summary>
    /// Whether the download line is visible on the speed graph
    /// </summary>
    public bool ShowDownloadLine { get; set; } = true;

    /// <summary>
    /// Whether the upload line is visible on the speed graph
    /// </summary>
    public bool ShowUploadLine { get; set; } = true;

    /// <summary>
    /// Column visibility overrides. Only stores non-default values.
    /// Key = column key (e.g., "Status"), Value = visible.
    /// </summary>
    public Dictionary<string, bool> ColumnVisibility { get; set; } = new();

    /// <summary>
    /// Persisted column widths for the torrent list. Key = column Key, Value = pixel width.
    /// Name column excluded (always flex). Null = use auto-fit defaults.
    /// </summary>
    public Dictionary<string, double>? ColumnWidths { get; set; }

    /// <summary>
    /// Schema version for future migrations
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Check if sort column is a valid column name
    /// </summary>
    public bool HasValidSortColumn()
    {
        var validColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Name", "Progress", "Size", "TimeLeft", "Seeds", "Peers",
            "DownloadRate", "UploadRate", "AddedOn", "CompletedOn",
            "State", "Ratio", "TotalDone", "Uploaded", "Availability",
            "ActiveDuration", "SeedingDuration", "SavePath", "CategoryName", "TagsDisplay"
        };
        return validColumns.Contains(SortColumn);
    }

    /// <summary>
    /// Check if active section is valid
    /// </summary>
    public bool HasValidSection()
    {
        var validSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "All", "Downloading", "Seeding", "Completed", "Paused", "Error"
        };
        return validSections.Contains(ActiveSection);
    }
}
