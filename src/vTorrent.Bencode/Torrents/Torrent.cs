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
    public class Torrent
    {
        public string Announce { get; init; }

        public TorrentInfo Info { get; init; }

        public DateTimeOffset? CreationDate { get; init; }

        public string? Comment { get; init; }

        public string? CreatedBy { get; init; }

        public Encoding Encoding { get; init; } = Encoding.UTF8;

        public IReadOnlyList<IReadOnlyList<string>>? AnnounceList { get; init; }

        public IReadOnlyDictionary<SHA256Hash, byte[]>? PieceLayers { get; init; }

        /// <summary>BEP 19: URL seed list (GetRight-style web seeds). Outside info dict.</summary>
        public IReadOnlyList<string>? UrlList { get; init; }

        /// <summary>BEP 17: HTTP seed list (smart-server web seeds). Outside info dict.</summary>
        public IReadOnlyList<string>? HttpSeeds { get; init; }

        public IEnumerable<string> GetAllTrackers()
        {
            if (!string.IsNullOrEmpty(Announce))
                yield return Announce;

            if (AnnounceList != null)
            {
                foreach (var tier in AnnounceList)
                    foreach (var tracker in tier)
                        if (tracker != Announce)  
                            yield return tracker;
            }
        }

        public string DisplayName => Info.Name;

        public long TotalSize => Info.TotalSize;

        public int PieceCount => Info.Pieces?.Count ?? 0;

        internal byte[]? _cachedInfoHash;

        public byte[] GetInfoHashBytes()
        {
            if (_cachedInfoHash == null)
            {
                var infoDict = Info.ToBDictionary(Encoding);
                var infoBytes = infoDict.EncodeAsBytes();
                _cachedInfoHash = SHA1.HashData(infoBytes);
            }
            return _cachedInfoHash;
        }

        public string GetInfoHashHex()
        {
            return Convert.ToHexString(GetInfoHashBytes());
        }

        public InfoHash GetInfoHash()
        {
            var infoDict = Info.ToBDictionary(Encoding);
            var infoBytes = infoDict.EncodeAsBytes();

            return new InfoHash
            {
                V1 = Info.Pieces is not null
                    ? new SHA1Hash(SHA1.HashData(infoBytes))
                    : null,
                V2 = Info.FileTreeV2 is not null
                    ? new SHA256Hash(SHA256.HashData(infoBytes))
                    : null,
            };
        }

        public string GetMagnetLink()
        {
            var sb = new StringBuilder();
            sb.Append("magnet:?xt=urn:btih:");
            sb.Append(GetInfoHashHex());

            if (!string.IsNullOrEmpty(DisplayName))
            {
                sb.Append("&dn=");
                sb.Append(Uri.EscapeDataString(DisplayName));
            }

            foreach (var tracker in GetAllTrackers())
            {
                sb.Append("&tr=");
                sb.Append(Uri.EscapeDataString(tracker));
            }

            return sb.ToString();
        }

        public BDictionary ToBDictionary()
        {
            var dict = new BDictionary();

            // Announce
            if (!string.IsNullOrEmpty(Announce))
                dict["announce"] = new BString(Announce, Encoding);

            // Announce-list
            if (AnnounceList != null && AnnounceList.Count > 0)
            {
                var announceList = new BList();
                foreach (var tier in AnnounceList)
                {
                    var tierList = new BList();
                    foreach (var tracker in tier)
                        tierList.Add(new BString(tracker, Encoding));
                    announceList.Add(tierList);
                }
                dict["announce-list"] = announceList;
            }

            // Creation date
            if (CreationDate.HasValue)
                dict["creation date"] = new BNumber(CreationDate.Value.ToUnixTimeSeconds());

            // Comment
            if (!string.IsNullOrEmpty(Comment))
                dict["comment"] = new BString(Comment, Encoding);

            // Created by
            if (!string.IsNullOrEmpty(CreatedBy))
                dict["created by"] = new BString(CreatedBy, Encoding);

            // Encoding
            if (Encoding != Encoding.UTF8)
                dict["encoding"] = new BString(Encoding.WebName, Encoding.ASCII);

            // Info dictionary
            dict["info"] = Info.ToBDictionary(Encoding);

            // Piece layers (v2/hybrid — outside info dict per BEP 52)
            if (PieceLayers is not null && PieceLayers.Count > 0)
            {
                var layersDict = new BDictionary();
                foreach (var (root, data) in PieceLayers)
                    layersDict[new BString(root.Bytes)] = new BString(data);
                dict["piece layers"] = layersDict;
            }

            // BEP 19: url-list
            if (UrlList is not null && UrlList.Count > 0)
            {
                if (UrlList.Count == 1)
                {
                    dict["url-list"] = new BString(UrlList[0], Encoding);
                }
                else
                {
                    var urlListBList = new BList();
                    foreach (var url in UrlList)
                        urlListBList.Add(new BString(url, Encoding));
                    dict["url-list"] = urlListBList;
                }
            }

            // BEP 17: httpseeds
            if (HttpSeeds is not null && HttpSeeds.Count > 0)
            {
                var httpSeedsList = new BList();
                foreach (var url in HttpSeeds)
                    httpSeedsList.Add(new BString(url, Encoding));
                dict["httpseeds"] = httpSeedsList;
            }

            return dict;
        }
        public static Torrent FromBDictionary(BDictionary dict)
        {
            return TorrentParser.FromBDictionary(dict);
        }

        public static Torrent FromBDictionaryStrict(BDictionary dict)
        {
            return TorrentParser.FromBDictionary(dict, TorrentParserMode.Strict);
        }

        public void Validate()
        {
            if (Info == null)
                throw new InvalidTorrentException("Info dictionary is required");

            Info.Validate();

            // Trackerless torrents (DHT-only) are valid per BEP 3
        }
    }
}
