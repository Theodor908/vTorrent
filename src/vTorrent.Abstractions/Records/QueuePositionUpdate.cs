namespace vTorrent.Abstractions.Records;

/// <summary>
/// Data for batch updating queue positions
/// </summary>
public class QueuePositionUpdate
{
    public string InfoHash { get; init; } = string.Empty;
    public int Position { get; init; }

    public QueuePositionUpdate(string infoHash, int position)
    {
        InfoHash = infoHash;
        Position = position;
    }
}
