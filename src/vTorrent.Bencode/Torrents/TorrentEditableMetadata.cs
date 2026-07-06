using System.Collections.Generic;

namespace vTorrent.Bencode.Torrents
{
    /// <summary>
    /// Mutable DTO for editable torrent metadata fields.
    /// Used by TorrentEditor for load -> edit -> save workflows.
    /// </summary>
    public sealed class TorrentEditableMetadata
    {
        /// <summary>Info dict name — changing this changes the info hash.</summary>
        public string Name { get; set; } = "";

        /// <summary>Comment — outside info dict, does not change hash.</summary>
        public string? Comment { get; set; }

        /// <summary>Source tag — inside info dict, changes hash. Used by private trackers.</summary>
        public string? Source { get; set; }

        /// <summary>Private flag — inside info dict, changes hash.</summary>
        public bool IsPrivate { get; set; }

        /// <summary>Tracker tiers — outside info dict. Outer list = tiers, inner = trackers in tier.</summary>
        public List<List<string>> Trackers { get; set; } = new();

        /// <summary>BEP 19 URL seeds — outside info dict.</summary>
        public List<string> UrlSeeds { get; set; } = new();

        /// <summary>BEP 17 HTTP seeds — outside info dict.</summary>
        public List<string> HttpSeeds { get; set; } = new();
    }

    /// <summary>
    /// Read-only metadata extracted from a .torrent file for display.
    /// </summary>
    public sealed class TorrentReadOnlyMetadata
    {
        public string? InfoHashV1 { get; init; }
        public string? InfoHashV2 { get; init; }
        public long TotalSize { get; init; }
        public long PieceSize { get; init; }
        public int PieceCount { get; init; }
        public int FileCount { get; init; }
        public TorrentVersion Format { get; init; }
    }
}
