using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Builders
{
    public sealed class TorrentBuilder
    {
        private string? _announce;
        private List<List<string>>? _announceList;
        private string? _name;
        private long _pieceLength;
        private PieceHashes? _pieces;
        private List<TorrentFile>? _files;
        private bool _isPrivate;
        private string? _source;
        private DateTimeOffset? _creationDate;
        private string? _comment;
        private string? _createdBy;
        private Encoding _encoding = Encoding.UTF8;
        private List<string>? _urlSeeds;
        private List<string>? _httpSeeds;
        private FileTree? _fileTree;
        private int? _metaVersion;
        private Dictionary<SHA256Hash, byte[]>? _pieceLayers;

        public TorrentBuilder WithAnnounce(string url)
        {
            _announce = url;
            return this;
        }

        public TorrentBuilder WithTrackerTier(params string[] trackers)
        {
            _announceList ??= new List<List<string>>();
            _announceList.Add(new List<string>(trackers));
            return this;
        }

        public TorrentBuilder WithName(string name)
        {
            _name = name;
            return this;
        }

        public TorrentBuilder WithPieceLength(long bytes)
        {
            _pieceLength = bytes;
            return this;
        }

        public TorrentBuilder WithPieces(PieceHashes pieces)
        {
            _pieces = pieces;
            return this;
        }

        public TorrentBuilder WithSingleFile(string fileName, long fileSize)
        {
            _name ??= fileName;
            _files = new List<TorrentFile>
            {
                new TorrentFile
                {
                    Path = new[] { fileName },
                    Length = fileSize
                }
            };
            return this;
        }

        public TorrentBuilder WithMultipleFiles(string directoryName, params TorrentFile[] files)
        {
            _name = directoryName;
            _files = new List<TorrentFile>(files);
            return this;
        }

        public TorrentBuilder AsPrivate(bool isPrivate = true)
        {
            _isPrivate = isPrivate;
            return this;
        }

        public TorrentBuilder WithSource(string source)
        {
            _source = source;
            return this;
        }

        public TorrentBuilder WithCreationDate(DateTimeOffset date)
        {
            _creationDate = date;
            return this;
        }

        public TorrentBuilder WithComment(string comment)
        {
            _comment = comment;
            return this;
        }

        public TorrentBuilder WithCreatedBy(string creator)
        {
            _createdBy = creator;
            return this;
        }

        public TorrentBuilder WithEncoding(Encoding encoding)
        {
            _encoding = encoding;
            return this;
        }

        public TorrentBuilder WithUrlSeeds(params string[] urls)
        {
            _urlSeeds = new List<string>(urls);
            return this;
        }

        public TorrentBuilder WithHttpSeeds(params string[] urls)
        {
            _httpSeeds = new List<string>(urls);
            return this;
        }

        public TorrentBuilder WithFileTree(FileTree fileTree)
        {
            _fileTree = fileTree;
            return this;
        }

        public TorrentBuilder WithMetaVersion(int version)
        {
            _metaVersion = version;
            return this;
        }

        public TorrentBuilder WithPieceLayers(Dictionary<SHA256Hash, byte[]> layers)
        {
            _pieceLayers = layers;
            return this;
        }

        public Torrent Build()
        {
            if (string.IsNullOrEmpty(_name))
                throw new InvalidOperationException("Torrent name is required");
            if (_pieceLength <= 0)
                throw new InvalidOperationException("Piece length must be positive");
            if (_pieces == null && _fileTree == null)
                throw new InvalidOperationException("Pieces (v1) or file tree (v2) is required");
            if (_files == null || _files.Count == 0)
                throw new InvalidOperationException("At least one file is required");

            var info = new TorrentInfo
            {
                Name = _name,
                PieceLength = _pieceLength,
                Pieces = _pieces,
                IsPrivate = _isPrivate,
                Source = _source,
                Files = _files.AsReadOnly(),
                MetaVersion = _metaVersion,
                FileTreeV2 = _fileTree,
            };

            var torrent = new Torrent
            {
                Announce = _announce ?? "",
                AnnounceList = _announceList?.Select(t => (IReadOnlyList<string>)t.AsReadOnly()).ToList().AsReadOnly(),
                Info = info,
                CreationDate = _creationDate,
                Comment = _comment,
                CreatedBy = _createdBy,
                Encoding = _encoding,
                UrlList = _urlSeeds?.AsReadOnly(),
                HttpSeeds = _httpSeeds?.AsReadOnly(),
                PieceLayers = _pieceLayers,
            };

            torrent.Validate();
            return torrent;
        }
    }
}
