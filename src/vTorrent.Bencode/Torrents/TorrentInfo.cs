using vTorrent.Bencode.Exceptions;
using vTorrent.Bencode.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Torrents
{
    public sealed class TorrentInfo
    {
        public string Name { get; init; }

        public long PieceLength { get; init; }

        public PieceHashes Pieces { get; init; }

        public int PieceCount => Pieces?.Count ?? 0;

        public bool IsPrivate { get; init; }

        public string? Source { get; init; }

        public TorrentFileMode FileMode => Files.Count == 1 ? TorrentFileMode.Single : TorrentFileMode.Multi;

        public IReadOnlyList<TorrentFile> Files { get; init; }

        // v2 fields (nullable — null for pure v1 torrents)
        public int? MetaVersion { get; init; }
        public FileTree? FileTreeV2 { get; init; }

        public TorrentVersion Version => (Pieces, FileTreeV2) switch
        {
            (not null, not null) => TorrentVersion.Hybrid,
            (not null, null) => TorrentVersion.V1,
            (null, not null) => TorrentVersion.V2,
            _ => throw new InvalidOperationException("TorrentInfo has neither v1 pieces nor v2 file tree")
        };

        public long TotalSize => Files.Sum(f => f.Length);

        public BDictionary ToBDictionary(Encoding encoding)
        {
            if (encoding == null)
                throw new ArgumentNullException(nameof(encoding));

            var dict = new BDictionary();

            // Name (required for all versions)
            dict["name"] = new BString(Name, encoding);

            // Piece length (required for all versions)
            dict["piece length"] = new BNumber(PieceLength);

            // v1 pieces (present for v1 and hybrid)
            if (Pieces is not null)
                dict["pieces"] = new BString(Pieces.ToByteArray(), encoding);

            // Private (optional)
            if (IsPrivate)
                dict["private"] = new BNumber(1);

            // Source (optional — private tracker convention, lives in info dict to affect info hash)
            if (!string.IsNullOrEmpty(Source))
                dict["source"] = new BString(Source, encoding);

            // v2 fields (present for v2 and hybrid)
            if (FileTreeV2 is not null)
            {
                dict["meta version"] = new BNumber(MetaVersion ?? 2);
                dict["file tree"] = FileTreeSerializer.Serialize(FileTreeV2);
            }

            // v1 file structure (present for v1 and hybrid, not for pure v2)
            if (Pieces is not null)
            {
                if (FileMode == TorrentFileMode.Single)
                {
                    var file = Files[0];
                    dict["length"] = new BNumber(file.Length);
                    if (!string.IsNullOrEmpty(file.Md5Sum))
                        dict["md5sum"] = new BString(file.Md5Sum, encoding);
                }
                else
                {
                    var filesList = new BList();
                    foreach (var file in Files)
                    {
                        var fileDict = new BDictionary
                        {
                            ["length"] = new BNumber(file.Length)
                        };
                        var pathList = new BList();
                        foreach (var pathComponent in file.Path)
                            pathList.Add(new BString(pathComponent, encoding));
                        fileDict["path"] = pathList;
                        if (!string.IsNullOrEmpty(file.Md5Sum))
                            fileDict["md5sum"] = new BString(file.Md5Sum, encoding);
                        filesList.Add(fileDict);
                    }
                    dict["files"] = filesList;
                }
            }

            return dict;
        }
        public static TorrentInfo FromBDictionary(BDictionary dict, Encoding encoding)
        {
            if (dict == null)
                throw new ArgumentNullException(nameof(dict));
            if (encoding == null)
                throw new ArgumentNullException(nameof(encoding));

            // Parse required fields
            var name = dict.GetStringOrDefault("name");
            if (string.IsNullOrEmpty(name))
                throw InvalidTorrentException.MissingField("info.name");

            var pieceLength = dict.GetNumberOrDefault("piece length", -1);
            if (pieceLength <= 0)
                throw InvalidTorrentException.ForField("info.piece length", "must be positive");

            // Parse v1 pieces (conditional — not required for v2-only)
            PieceHashes? pieces = null;
            var piecesBytes = dict.GetOrDefault<BString>("pieces")?.Value.ToArray();
            if (piecesBytes is not null && piecesBytes.Length > 0)
            {
                if (piecesBytes.Length % 20 != 0)
                    throw InvalidTorrentException.ForField("info.pieces", "must be multiple of 20 bytes");
                pieces = new PieceHashes(piecesBytes);
            }

            // Parse optional private flag
            var isPrivate = dict.GetNumberOrDefault("private", 0) == 1;

            // Parse optional source tag (private tracker convention)
            var source = dict.GetStringOrDefault("source");

            // Parse v2 fields (additive)
            int? metaVersion = dict.ContainsKey("meta version")
                ? (int?)dict.GetNumber("meta version")
                : null;

            FileTree? fileTreeV2 = null;
            if (dict.ContainsKey("file tree"))
            {
                var fileTreeDict = dict.GetDictionary("file tree");
                fileTreeV2 = FileTreeParser.Parse(fileTreeDict);
            }

            // Must have either pieces (v1) or file tree (v2)
            if (pieces is null && fileTreeV2 is null)
                throw new InvalidTorrentException("Info dictionary must have 'pieces' (v1) or 'file tree' (v2)");

            // Parse files (single or multi-file mode)
            List<TorrentFile> files;

            if (dict.ContainsKey("length"))
            {
                // Single-file mode
                var length = dict.GetNumber("length");
                var md5sum = dict.GetStringOrDefault("md5sum");

                files = new List<TorrentFile>
                {
                    new TorrentFile
                    {
                        Path = new[] { name },
                        Length = length,
                        Md5Sum = md5sum
                    }
                };
            }
            else if (dict.ContainsKey("files"))
            {
                // Multi-file mode
                var filesList = dict.GetList("files");
                files = new List<TorrentFile>();

                foreach (var fileObj in filesList)
                {
                    if (fileObj is not BDictionary fileDict)
                        throw InvalidTorrentException.ForField("info.files", "must contain dictionaries");

                    var length = fileDict.GetNumber("length");
                    var pathList = fileDict.GetList("path");
                    var md5sum = fileDict.GetStringOrDefault("md5sum");

                    var pathComponents = new List<string>();
                    foreach (var pathObj in pathList)
                    {
                        if (pathObj is not BString pathString)
                            throw InvalidTorrentException.ForField("info.files[].path", "must contain strings");
                        pathComponents.Add(pathString.ToString());
                    }

                    if (pathComponents.Count == 0)
                        throw InvalidTorrentException.ForField("info.files[].path", "cannot be empty");

                    files.Add(new TorrentFile
                    {
                        Path = pathComponents,
                        Length = length,
                        Md5Sum = md5sum
                    });
                }

                if (files.Count == 0)
                    throw InvalidTorrentException.ForField("info.files", "must contain at least one file");
            }
            else if (fileTreeV2 is not null)
            {
                // v2-only: populate files from file tree
                files = FileTreeParser.Flatten(fileTreeV2).ToList();
            }
            else
            {
                throw new InvalidTorrentException("Info dictionary must contain either 'length' (single-file), 'files' (multi-file), or 'file tree' (v2)");
            }

            var info = new TorrentInfo
            {
                Name = name,
                PieceLength = pieceLength,
                Pieces = pieces,
                IsPrivate = isPrivate,
                Source = source,
                Files = files.AsReadOnly(),
                MetaVersion = metaVersion,
                FileTreeV2 = fileTreeV2,
            };

            info.Validate();
            return info;
        }

        public void Validate()
        {
            if (string.IsNullOrEmpty(Name))
                throw new InvalidTorrentException("Name is required");

            if (PieceLength <= 0)
                throw new InvalidTorrentException("Piece length must be positive");

            if (Pieces is null && FileTreeV2 is null)
                throw new InvalidTorrentException("Must have v1 pieces or v2 file tree");

            if (Files == null || Files.Count == 0)
                throw new InvalidTorrentException("At least one file is required");

            // BEP 52: piece length must be power of 2 and >= 16KiB for v2
            if (MetaVersion == 2)
            {
                if (PieceLength < 16384 || (PieceLength & (PieceLength - 1)) != 0)
                    throw new InvalidTorrentException("v2 piece length must be a power of 2 and >= 16 KiB");
            }
        }
    }
}
