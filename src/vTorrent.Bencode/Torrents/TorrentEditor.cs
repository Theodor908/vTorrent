using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;

namespace vTorrent.Bencode.Torrents
{
    /// <summary>
    /// Edits .torrent files at the BDictionary level, preserving unknown keys.
    /// </summary>
    public static class TorrentEditor
    {
        public static BDictionary LoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path cannot be null or empty", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("Torrent file not found", path);

            var bytes = File.ReadAllBytes(path);
            var parser = new BencodeParser();
            var result = parser.Parse(bytes.AsSpan(), out _);
            return result as BDictionary
                ?? throw new InvalidOperationException("Torrent file does not contain a dictionary");
        }

        public static TorrentEditableMetadata GetEditableMetadata(BDictionary dict)
        {
            var info = dict.GetDictionary("info");

            var metadata = new TorrentEditableMetadata
            {
                Name = info.GetString("name"),
                Source = info.GetStringOrDefault("source"),
                IsPrivate = info.GetNumberOrDefault("private", 0) == 1,
                Comment = dict.GetStringOrDefault("comment"),
            };

            // Parse trackers
            var announceList = dict.GetListOrDefault("announce-list");
            if (announceList != null)
            {
                foreach (var tierObj in announceList)
                {
                    if (tierObj is BList tier)
                    {
                        var urls = new List<string>();
                        foreach (var item in tier)
                        {
                            if (item is BString url)
                                urls.Add(url.ToString());
                        }
                        if (urls.Count > 0)
                            metadata.Trackers.Add(urls);
                    }
                }
            }
            else
            {
                var announce = dict.GetStringOrDefault("announce");
                if (!string.IsNullOrEmpty(announce))
                    metadata.Trackers.Add(new List<string> { announce });
            }

            // Parse web seeds
            if (dict.ContainsKey("url-list"))
            {
                var urlListObj = dict["url-list"];
                if (urlListObj is BString single)
                    metadata.UrlSeeds.Add(single.ToString());
                else if (urlListObj is BList list)
                    foreach (var item in list)
                        if (item is BString s) metadata.UrlSeeds.Add(s.ToString());
            }

            if (dict.ContainsKey("httpseeds"))
            {
                var httpList = dict.GetListOrDefault("httpseeds");
                if (httpList != null)
                    foreach (var item in httpList)
                        if (item is BString s) metadata.HttpSeeds.Add(s.ToString());
            }

            return metadata;
        }

        public static TorrentReadOnlyMetadata GetReadOnlyMetadata(BDictionary dict)
        {
            var info = dict.GetDictionary("info");
            var hasPieces = info.ContainsKey("pieces");
            var hasFileTree = info.ContainsKey("file tree");

            var format = (hasPieces, hasFileTree) switch
            {
                (true, true) => TorrentVersion.Hybrid,
                (true, false) => TorrentVersion.V1,
                (false, true) => TorrentVersion.V2,
                _ => TorrentVersion.V1,
            };

            var pieceSize = info.GetNumber("piece length");
            long totalSize = 0;
            int fileCount = 0;

            if (info.ContainsKey("length"))
            {
                totalSize = info.GetNumber("length");
                fileCount = 1;
            }
            else if (info.ContainsKey("files"))
            {
                var files = info.GetList("files");
                fileCount = files.Count;
                foreach (var f in files)
                    if (f is BDictionary fd)
                        totalSize += fd.GetNumber("length");
            }
            else if (hasFileTree)
            {
                var tree = FileTreeParser.Parse(info.GetDictionary("file tree"));
                var flatFiles = FileTreeParser.Flatten(tree);
                fileCount = flatFiles.Count;
                totalSize = flatFiles.Sum(f => f.Length);
            }

            var pieceCount = totalSize > 0 ? (int)((totalSize + pieceSize - 1) / pieceSize) : 0;
            var (v1, v2) = RecalculateInfoHashes(dict);

            return new TorrentReadOnlyMetadata
            {
                InfoHashV1 = v1,
                InfoHashV2 = v2,
                TotalSize = totalSize,
                PieceSize = pieceSize,
                PieceCount = pieceCount,
                FileCount = fileCount,
                Format = format,
            };
        }

        public static void ApplyChanges(BDictionary dict, TorrentEditableMetadata metadata)
        {
            var encoding = Encoding.UTF8;
            var info = dict.GetDictionary("info");

            // Info dict fields (these change the hash)
            info["name"] = new BString(metadata.Name, encoding);

            if (!string.IsNullOrEmpty(metadata.Source))
                info["source"] = new BString(metadata.Source, encoding);
            else
                info.Remove("source");

            if (metadata.IsPrivate)
                info["private"] = new BNumber(1);
            else
                info.Remove("private");

            // Outer fields (don't change the hash)
            if (!string.IsNullOrEmpty(metadata.Comment))
                dict["comment"] = new BString(metadata.Comment, encoding);
            else
                dict.Remove("comment");

            // Trackers
            if (metadata.Trackers.Count > 0)
            {
                dict["announce"] = new BString(metadata.Trackers[0][0], encoding);

                var announceList = new BList();
                foreach (var tier in metadata.Trackers)
                {
                    var tierList = new BList();
                    foreach (var url in tier)
                        tierList.Add(new BString(url, encoding));
                    announceList.Add(tierList);
                }
                dict["announce-list"] = announceList;
            }
            else
            {
                dict.Remove("announce");
                dict.Remove("announce-list");
            }

            // URL seeds
            if (metadata.UrlSeeds.Count > 0)
            {
                if (metadata.UrlSeeds.Count == 1)
                    dict["url-list"] = new BString(metadata.UrlSeeds[0], encoding);
                else
                {
                    var list = new BList();
                    foreach (var url in metadata.UrlSeeds)
                        list.Add(new BString(url, encoding));
                    dict["url-list"] = list;
                }
            }
            else
            {
                dict.Remove("url-list");
            }

            // HTTP seeds
            if (metadata.HttpSeeds.Count > 0)
            {
                var list = new BList();
                foreach (var url in metadata.HttpSeeds)
                    list.Add(new BString(url, encoding));
                dict["httpseeds"] = list;
            }
            else
            {
                dict.Remove("httpseeds");
            }

        }

        public static void SaveToFile(BDictionary dict, string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path cannot be null or empty", nameof(path));

            dict.EncodeToFile(path);
        }

        public static (string? v1Hex, string? v2Hex) RecalculateInfoHashes(BDictionary dict)
        {
            var info = dict.GetDictionary("info");
            var infoBytes = info.EncodeAsBytes();

            string? v1 = null;
            string? v2 = null;

            if (info.ContainsKey("pieces"))
                v1 = Convert.ToHexString(SHA1.HashData(infoBytes));

            if (info.ContainsKey("file tree"))
                v2 = Convert.ToHexString(SHA256.HashData(infoBytes));

            return (v1, v2);
        }
    }
}
