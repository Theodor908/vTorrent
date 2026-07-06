namespace vTorrent.Abstractions.Records;

/// <summary>
/// Represents a torrent tag for flexible labeling.
/// Tags provide many-to-many labeling for torrents.
/// </summary>
public record Tag
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// Tag name (unique)
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Display color in hex format (e.g., "#3B82F6")
    /// </summary>
    public string? Color { get; init; }

    /// <summary>
    /// Sort order for display (lower = higher in list)
    /// </summary>
    public int SortOrder { get; init; }

    /// <summary>
    /// Unix timestamp when the tag was created
    /// </summary>
    public long CreatedAt { get; init; }

    /// <summary>
    /// Unix timestamp when the tag was last updated
    /// </summary>
    public long UpdatedAt { get; init; }
}
