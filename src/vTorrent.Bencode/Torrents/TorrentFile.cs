using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using vTorrent.Bencode.Exceptions;

namespace vTorrent.Bencode.Torrents
{
    public sealed record TorrentFile
    {
        public IReadOnlyList<string> Path { get; init; }

        public long Length { get; init; }

        public string? Md5Sum { get; init; }

        /// <summary>BEP 52: Per-file merkle tree root hash (null for v1-only files).</summary>
        public SHA256Hash? PiecesRoot { get; init; }

        public string GetFullPath() => string.Join(System.IO.Path.DirectorySeparatorChar, Path);

        public void Validate()
        {
            if (Path == null || Path.Count == 0)
                throw new InvalidTorrentException("File path is required");

            if (Length < 0)
                throw new InvalidTorrentException("File length cannot be negative");
        }
    }
}
