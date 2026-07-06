using vTorrent.Abstractions.Enums;

namespace vTorrent.Server.Models;

public record AddTorrentRequest
{
    public string? SavePath { get; init; }
    public bool StartImmediately { get; init; } = true;
    public bool SequentialDownload { get; init; }
    public bool FirstLastPiecePriority { get; init; }
    public bool AddToTopOfQueue { get; init; }
    public FilePriority[]? FilePriorities { get; init; }
}
