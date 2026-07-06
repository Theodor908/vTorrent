namespace vTorrent.Abstractions.Records;

/// <summary>
/// Database record for file information
/// </summary>
public class FileRecord
{
    public string InfoHash { get; set; } = string.Empty;
    public int FileIndex { get; set; }
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public int Priority { get; set; } = 4; // 0=skip, 1-7 priority
    public double Progress { get; set; }
}
