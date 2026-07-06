namespace vTorrent.Abstractions.Records;

/// <summary>
/// Represents a torrent category with an optional download path.
/// Categories provide folder-like organization for torrents.
/// </summary>
public record Category
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// Category name (unique)
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Display color in hex format (e.g., "#EF4444")
    /// </summary>
    public string? Color { get; init; }

    /// <summary>
    /// Default download folder for torrents in this category.
    /// If null, uses the global default save path.
    /// </summary>
    public string? SavePath { get; init; }

    /// <summary>
    /// Sort order for display (lower = higher in list)
    /// </summary>
    public int SortOrder { get; init; }

    /// <summary>
    /// Unix timestamp when the category was created
    /// </summary>
    public long CreatedAt { get; init; }

    /// <summary>
    /// Unix timestamp when the category was last updated
    /// </summary>
    public long UpdatedAt { get; init; }
}
