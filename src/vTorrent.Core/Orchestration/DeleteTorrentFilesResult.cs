using System.Collections.Generic;

namespace vTorrent.Core.Orchestration;

public class DeleteTorrentFilesResult
{
    public bool HasExtraFiles { get; init; }
    public IReadOnlyList<string> ExtraFiles { get; init; } = [];
    public string? TorrentDirectory { get; init; }
    public string? SavePath { get; init; }
}
