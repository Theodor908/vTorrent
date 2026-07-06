using vTorrent.Bencode.Torrents;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace vTorrent.Core.PieceIO
{
    public class PieceMapper
    {

        private readonly string _basePath;
        private readonly TorrentInfo _torrentInfo;
        private readonly List<FileMapping> _fileMappings;

        internal IReadOnlyList<FileMapping> FileMappings => _fileMappings;
        internal long PieceLength => _torrentInfo.PieceLength;

        public PieceMapper(string basePath, TorrentInfo torrentInfo)
        {
            _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
            _torrentInfo = torrentInfo ?? throw new ArgumentNullException(nameof(_torrentInfo));
            _fileMappings = BuildFileMappings();
        }

        public PieceLocation MapPieceToFiles(int pieceIndex)
        {
            if(pieceIndex < 0 || pieceIndex >= _torrentInfo.Pieces.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(pieceIndex), $"Piece index {pieceIndex} is out of range (0-{_torrentInfo.Pieces.Count - 1})");
            }

            var pieceSize = GetPieceSize(pieceIndex);
            var pieceStartOffset = (long)pieceIndex * _torrentInfo.PieceLength;
            var pieceEndOffset = pieceStartOffset + pieceSize;

            var location = new PieceLocation
            {
                PieceIndex = pieceIndex,
                PieceSize = pieceSize,
            };

            foreach (var fileMapping in _fileMappings) 
            {
                if(pieceStartOffset < fileMapping.EndOffset && pieceEndOffset > fileMapping.StartOffset)
                {
                    var segmentStart = Math.Max(pieceStartOffset, fileMapping.StartOffset);
                    var segmentEnd = Math.Min(pieceEndOffset, fileMapping.EndOffset);
                    var segmentLength = segmentEnd - segmentStart;

                    var segment = new FileSegment
                    {
                        FilePath = fileMapping.FilePath,
                        FileOffset = segmentStart - fileMapping.StartOffset,
                        PieceOffset = segmentStart - pieceStartOffset,
                        Length = segmentLength,
                        FileIndex = fileMapping.FileIndex
                    };

                    location.FileSegments.Add(segment);
                }

                if(fileMapping.StartOffset >= pieceEndOffset)
                {
                    break;
                }

            }
                return location;
        }

        public long GetPieceSize(int pieceIndex)
        {
            if(pieceIndex < 0 || pieceIndex >= _torrentInfo.Pieces.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(pieceIndex));
            }

            if(pieceIndex == _torrentInfo.Pieces.Count - 1)
            {
                var lastPieceSize = _torrentInfo.TotalSize % _torrentInfo.PieceLength;
                return lastPieceSize == 0 ? _torrentInfo.PieceLength : lastPieceSize;
            }

            return _torrentInfo.PieceLength;
        }

        private List<FileMapping> BuildFileMappings()
        {
            var mappings = new List<FileMapping>();
            long currentOffset = 0;

            // Check FileMode to properly distinguish single vs multi-file torrents
            // Single-file: Files.Count == 1, path is just the filename
            // Multi-file: Files.Count > 1 (or == 1 with subdirectory), needs torrent name directory
            var isSingleFile = _torrentInfo.FileMode == Bencode.Torrents.TorrentFileMode.Single;

            if (isSingleFile)
            {
                // Single-file torrent: file is named after torrent name directly in base path
                var filePath = Path.Combine(_basePath, _torrentInfo.Name);
                mappings.Add(new FileMapping
                {
                    FilePath = filePath,
                    StartOffset = 0,
                    EndOffset = _torrentInfo.TotalSize,
                    Length = _torrentInfo.TotalSize,
                    FileIndex = 0
                });
            }
            else
            {
                // Multi-file torrent: create a directory named after the torrent
                // and place all files inside (libtorrent standard behavior)
                var torrentDir = Path.Combine(_basePath, _torrentInfo.Name);

                for (int i = 0; i < _torrentInfo.Files.Count; i++)
                {
                    var file = _torrentInfo.Files[i];
                    var filePath = BuildFilePath(torrentDir, file.Path);
                    mappings.Add(new FileMapping
                    {
                        FilePath = filePath,
                        StartOffset = currentOffset,
                        EndOffset = currentOffset + file.Length,
                        Length = file.Length,
                        FileIndex = i
                    });
                    currentOffset += file.Length;
                }
            }

            return mappings;
        }

        private string BuildFilePath(string basePath, IReadOnlyList<string> pathComponents)
        {
            if (pathComponents == null || pathComponents.Count == 0)
            {
                throw new ArgumentException("Path components cannot be null or empty");
            }

            var parts = new string[pathComponents.Count + 1];
            parts[0] = basePath;
            for(int i = 0; i < pathComponents.Count; i++)
            {
                parts[i + 1] = pathComponents[i];
            }

            return Path.Combine(parts);
        }

        internal class FileMapping
        {
            public string FilePath { get; set; }
            public long StartOffset { get; set; }
            public long EndOffset { get; set; }
            public long Length { get; set; }
            public int FileIndex { get; set; }
        }

    }
}
