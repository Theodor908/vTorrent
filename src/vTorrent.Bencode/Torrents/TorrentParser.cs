using vTorrent.Bencode.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using vTorrent.Bencode.Exceptions;

namespace vTorrent.Bencode.Torrents
{
    public static class TorrentParser
    {

        public static Torrent FromBDictionary(BDictionary dict, TorrentParserMode mode = TorrentParserMode.Tolerant)
        {
            if (dict == null)
                throw new ArgumentNullException(nameof(dict));

            if (mode == TorrentParserMode.Strict)
                ValidateRequiredFields(dict);

            return ParseTorrent(dict);
        }

        private static void ValidateRequiredFields(BDictionary dict)
        {
            // Check for info dictionary
            if (!dict.ContainsKey("info"))
                throw InvalidTorrentException.MissingField("info");

            var info = dict.GetDictionary("info");

            // Required info fields (name and piece length always required)
            foreach (var field in new[] { "name", "piece length" })
            {
                if (!info.ContainsKey(field))
                    throw InvalidTorrentException.MissingField($"info.{field}");
            }

            // "pieces" is required for v1 but not for v2 (which uses "file tree")
            if (!info.ContainsKey("pieces") && !info.ContainsKey("file tree"))
                throw InvalidTorrentException.MissingField("info.pieces or info.file tree");

            // Must have either 'length' (single-file), 'files' (multi-file), or 'file tree' (v2)
            var hasLength = info.ContainsKey("length");
            var hasFiles = info.ContainsKey("files");
            var hasFileTree = info.ContainsKey("file tree");

            if (!hasLength && !hasFiles && !hasFileTree)
                throw new InvalidTorrentException("Info dictionary must contain 'length', 'files', or 'file tree'");

            if (hasLength && hasFiles)
                throw new InvalidTorrentException("Info dictionary cannot contain both 'length' and 'files'");

            // Validate files structure if multi-file
            if (hasFiles)
            {
                var filesList = info.GetList("files");
                foreach (var fileObj in filesList)
                {
                    if (fileObj is not BDictionary fileDict)
                        throw new InvalidTorrentException("Files list must contain dictionaries");

                    if (!fileDict.ContainsKey("length"))
                        throw InvalidTorrentException.MissingField("info.files[].length");

                    if (!fileDict.ContainsKey("path"))
                        throw InvalidTorrentException.MissingField("info.files[].path");
                }
            }
        }

        private static Torrent ParseTorrent(BDictionary dict)
        {
            // Parse encoding first (needed for string decoding)
            var encoding = ParseEncoding(dict.GetStringOrDefault("encoding")) ?? Encoding.UTF8;

            // Parse announce URLs
            var announce = dict.GetStringOrDefault("announce");
            var announceList = ParseAnnounceList(dict, encoding);

            // Parse metadata
            var creationDate = ParseCreationDate(dict.GetNumberOrDefault("creation date", -1));
            var comment = dict.GetStringOrDefault("comment");
            var createdBy = dict.GetStringOrDefault("created by");

            // Parse info dictionary
            var infoDict = dict.GetDictionary("info");
            var info = ParseTorrentInfo(infoDict, encoding);

            // Calculate and cache original info hash
            var infoBytes = infoDict.EncodeAsBytes();
            var infoHash = SHA1.HashData(infoBytes);

            // Parse piece layers (BEP 52 — outside info dict)
            Dictionary<SHA256Hash, byte[]>? pieceLayers = null;
            if (dict.ContainsKey("piece layers"))
            {
                var layersDict = dict.GetDictionary("piece layers");
                pieceLayers = new Dictionary<SHA256Hash, byte[]>();
                foreach (var kvp in layersDict)
                {
                    if (kvp.Key.Value.Length == SHA256Hash.Size && kvp.Value is BString layerData)
                    {
                        var root = new SHA256Hash(kvp.Key.Value.ToArray());
                        pieceLayers[root] = layerData.Value.ToArray();
                    }
                }
            }

            // BEP 19: url-list (can be BString for single URL or BList for multiple)
            List<string>? urlList = null;
            if (dict.ContainsKey("url-list"))
            {
                var urlListObj = dict["url-list"];
                urlList = new List<string>();
                if (urlListObj is BString singleUrl)
                {
                    var url = singleUrl.ToString();
                    if (IsValidWebSeedUrl(url))
                        urlList.Add(url);
                }
                else if (urlListObj is BList urlListArray)
                {
                    foreach (var item in urlListArray)
                    {
                        if (item is BString urlStr)
                        {
                            var url = urlStr.ToString();
                            if (IsValidWebSeedUrl(url))
                                urlList.Add(url);
                        }
                    }
                }
                if (urlList.Count == 0) urlList = null;
            }

            // BEP 17: httpseeds (always a list)
            List<string>? httpSeeds = null;
            if (dict.ContainsKey("httpseeds"))
            {
                var httpSeedsList = dict.GetListOrDefault("httpseeds");
                if (httpSeedsList != null)
                {
                    httpSeeds = new List<string>();
                    foreach (var item in httpSeedsList)
                    {
                        if (item is BString seedUrl)
                        {
                            var url = seedUrl.ToString();
                            if (IsValidWebSeedUrl(url))
                                httpSeeds.Add(url);
                        }
                    }
                    if (httpSeeds.Count == 0) httpSeeds = null;
                }
            }

            // Build torrent
            var torrent = new Torrent
            {
                Announce = announce,
                AnnounceList = announceList,
                Info = info,
                CreationDate = creationDate,
                Comment = comment,
                CreatedBy = createdBy,
                Encoding = encoding,
                PieceLayers = pieceLayers,
                UrlList = urlList?.AsReadOnly(),
                HttpSeeds = httpSeeds?.AsReadOnly(),
            };

            // Set the cached info hash via reflection or make it settable
            SetCachedInfoHash(torrent, infoHash);

            return torrent;
        }

        private static TorrentInfo ParseTorrentInfo(BDictionary infoDict, Encoding encoding)
        {
            var name = infoDict.GetString("name");
            var pieceLength = infoDict.GetNumber("piece length");

            // Parse v1 pieces (conditional — not required for v2-only)
            PieceHashes? pieces = null;
            var piecesBytes = infoDict.GetOrDefault<BString>("pieces")?.Value.ToArray();
            if (piecesBytes is not null && piecesBytes.Length > 0)
            {
                if (piecesBytes.Length % 20 != 0)
                    throw InvalidTorrentException.ForField("info.pieces", "must be multiple of 20 bytes");
                pieces = new PieceHashes(piecesBytes);
            }

            var isPrivate = infoDict.GetNumberOrDefault("private", 0) == 1;

            // Parse optional source tag (private tracker convention)
            var source = infoDict.GetStringOrDefault("source");

            // Parse v2 fields
            int? metaVersion = infoDict.ContainsKey("meta version")
                ? (int?)infoDict.GetNumber("meta version")
                : null;

            FileTree? fileTreeV2 = null;
            if (infoDict.ContainsKey("file tree"))
            {
                var fileTreeDict = infoDict.GetDictionary("file tree");
                fileTreeV2 = FileTreeParser.Parse(fileTreeDict);
            }

            // Must have either pieces (v1) or file tree (v2)
            if (pieces is null && fileTreeV2 is null)
                throw new InvalidTorrentException("Info dictionary must have 'pieces' (v1) or 'file tree' (v2)");

            List<TorrentFile> files;

            if (infoDict.ContainsKey("length"))
            {
                // Single-file mode
                var length = infoDict.GetNumber("length");
                var md5sum = infoDict.GetStringOrDefault("md5sum");

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
            else if (infoDict.ContainsKey("files"))
            {
                var filesList = infoDict.GetList("files");
                files = new List<TorrentFile>();

                foreach (var fileObj in filesList)
                {
                    if (fileObj is not BDictionary fileDict)
                        continue;

                    var length = fileDict.GetNumber("length");
                    var pathList = fileDict.GetList("path");
                    var md5sum = fileDict.GetStringOrDefault("md5sum");

                    var pathComponents = new List<string>();
                    foreach (var pathObj in pathList)
                    {
                        if (pathObj is BString pathString)
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
            }
            else if (fileTreeV2 is not null)
            {
                // v2-only: populate files from file tree
                files = FileTreeParser.Flatten(fileTreeV2).ToList();
            }
            else
            {
                throw new InvalidTorrentException("Info dictionary must contain 'length', 'files', or 'file tree'");
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

            return info;
        }

        // BEP 12: Multitracker Metadata Extension — parses the "announce-list" key
        // into a tiered list of tracker URLs (each inner list is a tier).
        private static List<List<string>>? ParseAnnounceList(BDictionary dict, Encoding encoding)
        {
            var announceListObj = dict.GetListOrDefault("announce-list");
            if (announceListObj == null)
                return null;

            var announceList = new List<List<string>>();

            // Standard format: list of lists
            if (announceListObj.All(x => x is BList))
            {
                foreach (var tierObj in announceListObj)
                {
                    if (tierObj is not BList tierList)
                        continue;

                    var tier = new List<string>();
                    foreach (var trackerObj in tierList)
                    {
                        if (trackerObj is BString trackerString)
                            tier.Add(trackerString.ToString());
                    }

                    if (tier.Count > 0)
                        announceList.Add(tier);
                }
            }

            else if (announceListObj.All(x => x is BString))
            {
                var tier = new List<string>();
                foreach (var trackerObj in announceListObj)
                {
                    if (trackerObj is BString trackerString)
                        tier.Add(trackerString.ToString());
                }

                if (tier.Count > 0)
                    announceList.Add(tier);
            }

            return announceList.Count > 0 ? announceList : null;
        }

        private static Encoding? ParseEncoding(string? encodingString)
        {
            if (string.IsNullOrEmpty(encodingString))
                return null;

            try
            {
                return Encoding.GetEncoding(encodingString);
            }
            catch (ArgumentException)
            {
                // Handle common variations
                if (string.Equals(encodingString, "UTF8", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(encodingString, "UTF-8", StringComparison.OrdinalIgnoreCase))
                {
                    return Encoding.UTF8;
                }

                return null;
            }
        }

        private static DateTimeOffset? ParseCreationDate(long timestamp)
        {
            if (timestamp <= 0)
                return null;

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(timestamp);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static bool IsValidWebSeedUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static void SetCachedInfoHash(Torrent torrent, byte[] infoHash)
        {
            // Use reflection to set the private cached info hash field
            var field = typeof(Torrent).GetField("_cachedInfoHash",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            field?.SetValue(torrent, infoHash);
        }
    }
}
