namespace vTorrent.Abstractions.Records;

/// <summary>
/// Database record for tracker URLs
/// </summary>
public class TrackerRecord
{
    public long Id { get; set; }
    public string InfoHash { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int Tier { get; set; }

    // State
    public string Status { get; set; } = "idle";
    public string? Message { get; set; }

    // Announce data
    public long? LastAnnounce { get; set; }
    public long? NextAnnounce { get; set; }
    public int MinAnnounceInterval { get; set; } = 1800;
    public int AnnounceInterval { get; set; } = 1800;

    // Scrape data
    public long? LastScrape { get; set; }
    public int? Seeders { get; set; }
    public int? Leechers { get; set; }
    public int? Downloaded { get; set; }
}

/// <summary>
/// Database record for web seed URLs (BEP 17/19)
/// </summary>
public class WebSeedRecord
{
    public int Id { get; set; }
    public string InfoHash { get; set; } = "";
    public string Url { get; set; } = "";
    public string Type { get; set; } = "BEP19";
    public long AddedAt { get; set; }
}
