using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;

namespace vTorrent.Core.ResumeData;

/// <summary>
/// Serializes and deserializes TorrentResumeData using bencoding format.
/// Compatible with libtorrent resume data format.
/// </summary>
public static class ResumeDataSerializer
{
    #region Serialization

    /// <summary>
    /// Serialize resume data to bencoded byte array
    /// </summary>
    public static byte[] Serialize(TorrentResumeData data)
    {
        var dict = new BDictionary();

        // Identity
        dict.AddString("info_hash", data.InfoHash);
        dict.AddString("name", data.Name);
        if (!string.IsNullOrEmpty(data.Comment))
            dict.AddString("comment", data.Comment);
        if (!string.IsNullOrEmpty(data.CreatedBy))
            dict.AddString("created_by", data.CreatedBy);

        // Piece state
        if (data.HavePieces != null && data.HavePieces.Length > 0)
            dict.AddBytes("have_pieces", data.HavePieces);

        if (data.VerifiedPieces != null && data.VerifiedPieces.Length > 0)
            dict.AddBytes("verified_pieces", data.VerifiedPieces);

        if (data.UnfinishedPieces != null && data.UnfinishedPieces.Count > 0)
        {
            var unfinishedDict = new BDictionary();
            foreach (var (pieceIndex, state) in data.UnfinishedPieces)
            {
                var pieceDict = new BDictionary();
                pieceDict.AddNumber("piece_index", state.PieceIndex);
                pieceDict.AddNumber("block_size", state.BlockSize);
                pieceDict.AddNumber("block_count", state.BlockCount);
                pieceDict.AddNumber("bytes_downloaded", state.BytesDownloaded);
                if (state.HaveBlocks != null && state.HaveBlocks.Length > 0)
                    pieceDict.AddBytes("have_blocks", state.HaveBlocks);
                unfinishedDict.Add(pieceIndex.ToString(), pieceDict);
            }
            dict.Add("unfinished_pieces", unfinishedDict);
        }

        dict.AddNumber("piece_count", data.PieceCount);
        dict.AddNumber("piece_length", data.PieceLength);
        dict.AddNumber("block_size", data.BlockSize);
        if (data.CheckingCheckpoint.HasValue)
            dict.AddNumber("checking_checkpoint", data.CheckingCheckpoint.Value);

        // Statistics
        dict.AddNumber("total_uploaded", data.TotalUploaded);
        dict.AddNumber("total_downloaded", data.TotalDownloaded);
        dict.AddNumber("active_time", data.ActiveTimeSeconds);
        dict.AddNumber("finished_time", data.FinishedTimeSeconds);
        dict.AddNumber("seeding_time", data.SeedingTimeSeconds);

        // Timestamps
        dict.AddNumber("added_time", data.AddedTime);
        dict.AddNumber("completed_time", data.CompletedTime);
        dict.AddNumber("last_seen_complete", data.LastSeenComplete);
        dict.AddNumber("last_download", data.LastDownload);
        dict.AddNumber("last_upload", data.LastUpload);
        dict.AddNumber("last_saved", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Swarm data
        dict.AddNumber("num_complete", data.NumComplete);
        dict.AddNumber("num_incomplete", data.NumIncomplete);
        dict.AddNumber("num_downloaded", data.NumDownloaded);

        // Configuration
        dict.AddString("save_path", data.SavePath);
        if (!string.IsNullOrEmpty(data.TorrentFilePath))
            dict.AddString("torrent_file_path", data.TorrentFilePath);

        if (data.FilePriorities != null && data.FilePriorities.Count > 0)
        {
            var priorityList = new BList();
            var maxIndex = data.FilePriorities.Keys.Max();
            for (int i = 0; i <= maxIndex; i++)
            {
                priorityList.AddNumber(data.FilePriorities.TryGetValue(i, out var priority) ? priority : 4);
            }
            dict.Add("file_priorities", priorityList);
        }

        if (data.PiecePriorities != null && data.PiecePriorities.Length > 0)
            dict.AddBytes("piece_priorities", data.PiecePriorities);

        if (data.RenamedFiles != null && data.RenamedFiles.Count > 0)
        {
            var renamedDict = new BDictionary();
            foreach (var (index, newPath) in data.RenamedFiles)
            {
                renamedDict.AddString(index.ToString(), newPath);
            }
            dict.Add("renamed_files", renamedDict);
        }

        // State flags (packed into a single value)
        dict.AddNumber("flags", (long)data.Flags);
        dict.AddNumber("storage_mode", (int)data.StorageMode);

        // Legacy individual flags for backwards compatibility
        dict.AddNumber("is_paused", data.IsPaused ? 1 : 0);
        dict.AddNumber("user_paused", data.UserPaused ? 1 : 0);
        dict.AddNumber("sequential_download", data.SequentialDownload ? 1 : 0);
        dict.AddNumber("first_last_piece_priority", data.FirstLastPiecePriority ? 1 : 0);
        dict.AddNumber("auto_managed", data.AutoManaged ? 1 : 0);

        // Limits
        dict.AddNumber("max_uploads", data.MaxUploads);
        dict.AddNumber("max_connections", data.MaxConnections);
        dict.AddNumber("upload_limit", data.UploadLimit);
        dict.AddNumber("download_limit", data.DownloadLimit);

        // Trackers (list of lists for tiers)
        if (data.Trackers != null && data.Trackers.Count > 0)
        {
            var trackerList = new BList();
            foreach (var tier in data.Trackers)
            {
                var tierList = new BList();
                foreach (var url in tier)
                {
                    tierList.AddString(url);
                }
                trackerList.Add(tierList);
            }
            dict.Add("trackers", trackerList);
        }

        // DHT nodes
        if (data.DhtNodes != null && data.DhtNodes.Count > 0)
        {
            var nodeList = new BList();
            foreach (var node in data.DhtNodes)
            {
                nodeList.AddString(node);
            }
            dict.Add("dht_nodes", nodeList);
        }

        // Peers (compact format)
        if (data.Peers != null && data.Peers.Length > 0)
            dict.AddBytes("peers", data.Peers);

        if (data.Peers6 != null && data.Peers6.Length > 0)
            dict.AddBytes("peers6", data.Peers6);

        if (data.BannedPeers != null && data.BannedPeers.Length > 0)
            dict.AddBytes("banned_peers", data.BannedPeers);

        // Seeds
        if (data.HttpSeeds != null && data.HttpSeeds.Count > 0)
        {
            var seedList = new BList();
            foreach (var url in data.HttpSeeds)
            {
                seedList.AddString(url);
            }
            dict.Add("http_seeds", seedList);
        }

        if (data.UrlSeeds != null && data.UrlSeeds.Count > 0)
        {
            var seedList = new BList();
            foreach (var url in data.UrlSeeds)
            {
                seedList.AddString(url);
            }
            dict.Add("url_seeds", seedList);
        }

        // Queue position
        dict.AddNumber("queue_position", data.QueuePosition);

        // Embedded torrent file (libtorrent parity: eliminates separate .torrent read on startup)
        if (data.TorrentFileBytes != null && data.TorrentFileBytes.Length > 0)
            dict.AddBytes("torrent_file", data.TorrentFileBytes);

        // Encode to bytes
        var size = dict.GetSizeInBytes();
        var buffer = new byte[size];
        dict.EncodeTo(buffer.AsSpan());
        return buffer;
    }

    /// <summary>
    /// Save resume data to file with atomic write
    /// </summary>
    public static async Task SaveAsync(string path, TorrentResumeData data)
    {
        var bytes = Serialize(data);
        var tempPath = path + ".tmp";

        try
        {
            // Write to temp file
            await File.WriteAllBytesAsync(tempPath, bytes);

            // Atomic rename
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            // Cleanup temp file if still exists
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
            }
        }
    }

    #endregion

    #region Deserialization

    /// <summary>
    /// Deserialize resume data from bencoded byte array
    /// </summary>
    public static TorrentResumeData Deserialize(byte[] data)
    {
        var parser = new BencodeParser();
        var parsed = parser.Parse(data, out _);

        if (parsed is not BDictionary dict)
            throw new InvalidDataException("Resume data must be a bencoded dictionary");

        var resume = new TorrentResumeData
        {
            // Identity
            InfoHash = dict.GetStringOrDefault("info_hash", ""),
            Name = dict.GetStringOrDefault("name", ""),
            Comment = dict.GetStringOrDefault("comment"),
            CreatedBy = dict.GetStringOrDefault("created_by"),

            // Piece state
            HavePieces = GetBytesOrDefault(dict, "have_pieces"),
            VerifiedPieces = GetBytesOrDefault(dict, "verified_pieces"),
            PieceCount = (int)dict.GetNumberOrDefault("piece_count"),
            PieceLength = (int)dict.GetNumberOrDefault("piece_length"),
            BlockSize = (int)dict.GetNumberOrDefault("block_size", 16384),

            // Statistics
            TotalUploaded = dict.GetNumberOrDefault("total_uploaded"),
            TotalDownloaded = dict.GetNumberOrDefault("total_downloaded"),
            ActiveTimeSeconds = dict.GetNumberOrDefault("active_time"),
            FinishedTimeSeconds = dict.GetNumberOrDefault("finished_time"),
            SeedingTimeSeconds = dict.GetNumberOrDefault("seeding_time"),

            // Timestamps
            AddedTime = dict.GetNumberOrDefault("added_time"),
            CompletedTime = dict.GetNumberOrDefault("completed_time"),
            LastSeenComplete = dict.GetNumberOrDefault("last_seen_complete"),
            LastDownload = dict.GetNumberOrDefault("last_download"),
            LastUpload = dict.GetNumberOrDefault("last_upload"),
            LastSaved = dict.GetNumberOrDefault("last_saved"),

            // Swarm data
            NumComplete = (int)dict.GetNumberOrDefault("num_complete"),
            NumIncomplete = (int)dict.GetNumberOrDefault("num_incomplete"),
            NumDownloaded = (int)dict.GetNumberOrDefault("num_downloaded"),

            // Configuration
            SavePath = dict.GetStringOrDefault("save_path", ""),
            TorrentFilePath = dict.GetStringOrDefault("torrent_file_path"),
            PiecePriorities = GetBytesOrDefault(dict, "piece_priorities"),
            UserPaused = dict.GetNumberOrDefault("user_paused") == 1,

            // Limits
            MaxUploads = (int)dict.GetNumberOrDefault("max_uploads", -1),
            MaxConnections = (int)dict.GetNumberOrDefault("max_connections", -1),
            UploadLimit = (int)dict.GetNumberOrDefault("upload_limit", -1),
            DownloadLimit = (int)dict.GetNumberOrDefault("download_limit", -1),

            // Peers (compact format)
            Peers = GetBytesOrDefault(dict, "peers"),
            Peers6 = GetBytesOrDefault(dict, "peers6"),
            BannedPeers = GetBytesOrDefault(dict, "banned_peers"),

            // Queue position
            QueuePosition = (int)dict.GetNumberOrDefault("queue_position")
        };

        // Checking checkpoint (optional)
        var checkpointValue = dict.GetNumberOrDefault("checking_checkpoint", -1);
        if (checkpointValue >= 0)
            resume.CheckingCheckpoint = (int)checkpointValue;

        // Load state flags (with backward compatibility from legacy fields)
        var flagsValue = dict.GetNumberOrDefault("flags", -1);
        if (flagsValue >= 0)
        {
            // New format - use packed flags
            resume.Flags = (TorrentFlags)(ulong)flagsValue;
        }
        else
        {
            // Legacy format - build flags from individual fields
            var flags = TorrentFlags.DefaultFlags;
            if (dict.GetNumberOrDefault("is_paused") == 1) flags |= TorrentFlags.Paused;
            if (dict.GetNumberOrDefault("sequential_download") == 1) flags |= TorrentFlags.SequentialDownload;
            if (dict.GetNumberOrDefault("first_last_piece_priority") == 1) flags |= TorrentFlags.FirstLastPiecePriority;
            if (dict.GetNumberOrDefault("auto_managed", 1) == 0) flags &= ~TorrentFlags.AutoManaged;
            resume.Flags = flags;
        }

        // Load storage mode
        resume.StorageMode = (StorageMode)(int)dict.GetNumberOrDefault("storage_mode", 0);

        // Parse unfinished pieces
        var unfinishedDict = dict.GetDictionaryOrDefault("unfinished_pieces");
        if (unfinishedDict != null)
        {
            resume.UnfinishedPieces = new Dictionary<int, UnfinishedPieceState>();
            foreach (var (key, value) in unfinishedDict)
            {
                if (int.TryParse(key.ToString(), out var pieceIndex) && value is BDictionary pieceDict)
                {
                    resume.UnfinishedPieces[pieceIndex] = new UnfinishedPieceState
                    {
                        PieceIndex = (int)pieceDict.GetNumberOrDefault("piece_index", pieceIndex),
                        BlockSize = (int)pieceDict.GetNumberOrDefault("block_size", 16384),
                        BlockCount = (int)pieceDict.GetNumberOrDefault("block_count"),
                        BytesDownloaded = (int)pieceDict.GetNumberOrDefault("bytes_downloaded"),
                        HaveBlocks = GetBytesOrDefault(pieceDict, "have_blocks")
                    };
                }
            }
        }

        // Parse file priorities
        var priorityList = dict.GetListOrDefault("file_priorities");
        if (priorityList != null)
        {
            resume.FilePriorities = new Dictionary<int, int>();
            for (int i = 0; i < priorityList.Count; i++)
            {
                if (priorityList[i] is BNumber num)
                {
                    resume.FilePriorities[i] = (int)num.Value;
                }
            }
        }

        // Parse renamed files
        var renamedDict = dict.GetDictionaryOrDefault("renamed_files");
        if (renamedDict != null)
        {
            resume.RenamedFiles = new Dictionary<int, string>();
            foreach (var (key, value) in renamedDict)
            {
                if (int.TryParse(key.ToString(), out var index) && value is BString str)
                {
                    resume.RenamedFiles[index] = str.ToString();
                }
            }
        }

        // Parse trackers
        var trackerList = dict.GetListOrDefault("trackers");
        if (trackerList != null)
        {
            resume.Trackers = new List<List<string>>();
            foreach (var tierItem in trackerList)
            {
                if (tierItem is BList tierList)
                {
                    var tier = new List<string>();
                    foreach (var urlItem in tierList)
                    {
                        if (urlItem is BString url)
                        {
                            tier.Add(url.ToString());
                        }
                    }
                    if (tier.Count > 0)
                        resume.Trackers.Add(tier);
                }
            }
        }

        // Parse DHT nodes
        var nodeList = dict.GetListOrDefault("dht_nodes");
        if (nodeList != null)
        {
            resume.DhtNodes = new List<string>();
            foreach (var node in nodeList)
            {
                if (node is BString nodeStr)
                {
                    resume.DhtNodes.Add(nodeStr.ToString());
                }
            }
        }

        // Parse seeds
        var httpSeedList = dict.GetListOrDefault("http_seeds");
        if (httpSeedList != null)
        {
            resume.HttpSeeds = new List<string>();
            foreach (var seed in httpSeedList)
            {
                if (seed is BString seedStr)
                {
                    resume.HttpSeeds.Add(seedStr.ToString());
                }
            }
        }

        var urlSeedList = dict.GetListOrDefault("url_seeds");
        if (urlSeedList != null)
        {
            resume.UrlSeeds = new List<string>();
            foreach (var seed in urlSeedList)
            {
                if (seed is BString seedStr)
                {
                    resume.UrlSeeds.Add(seedStr.ToString());
                }
            }
        }

        // Embedded torrent file (libtorrent parity)
        var torrentFileBytes = GetBytesOrDefault(dict, "torrent_file");
        if (torrentFileBytes != null && torrentFileBytes.Length > 0)
            resume.TorrentFileBytes = torrentFileBytes;

        return resume;
    }

    /// <summary>
    /// Load resume data from file
    /// </summary>
    public static async Task<TorrentResumeData> LoadAsync(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Resume file not found", path);

        var bytes = await File.ReadAllBytesAsync(path);
        return Deserialize(bytes);
    }

    /// <summary>
    /// Try to load resume data from file, returns null if not found or invalid
    /// </summary>
    public static async Task<TorrentResumeData?> TryLoadAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var bytes = await File.ReadAllBytesAsync(path);
            return Deserialize(bytes);
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Peer Serialization Helpers

    /// <summary>
    /// Serialize peers to compact format (6 bytes per IPv4 peer: 4 bytes IP + 2 bytes port)
    /// </summary>
    public static byte[] SerializePeersCompact(IEnumerable<SavedPeerInfo> peers)
    {
        var ipv4Peers = peers.Where(p => IsIPv4(p.IpAddress)).ToList();
        if (ipv4Peers.Count == 0)
            return Array.Empty<byte>();

        var buffer = new byte[ipv4Peers.Count * 6];
        var offset = 0;

        foreach (var peer in ipv4Peers)
        {
            if (IPAddress.TryParse(peer.IpAddress, out var ip))
            {
                var ipBytes = ip.GetAddressBytes();
                Buffer.BlockCopy(ipBytes, 0, buffer, offset, 4);
                offset += 4;

                buffer[offset++] = (byte)(peer.Port >> 8);
                buffer[offset++] = (byte)(peer.Port & 0xFF);
            }
        }

        return buffer[..offset];
    }

    /// <summary>
    /// Serialize IPv6 peers to compact format (18 bytes per peer: 16 bytes IP + 2 bytes port)
    /// </summary>
    public static byte[] SerializePeers6Compact(IEnumerable<SavedPeerInfo> peers)
    {
        var ipv6Peers = peers.Where(p => !IsIPv4(p.IpAddress)).ToList();
        if (ipv6Peers.Count == 0)
            return Array.Empty<byte>();

        var buffer = new byte[ipv6Peers.Count * 18];
        var offset = 0;

        foreach (var peer in ipv6Peers)
        {
            if (IPAddress.TryParse(peer.IpAddress, out var ip))
            {
                var ipBytes = ip.GetAddressBytes();
                Buffer.BlockCopy(ipBytes, 0, buffer, offset, 16);
                offset += 16;

                buffer[offset++] = (byte)(peer.Port >> 8);
                buffer[offset++] = (byte)(peer.Port & 0xFF);
            }
        }

        return buffer[..offset];
    }

    /// <summary>
    /// Deserialize compact peer format to peer list
    /// </summary>
    public static List<SavedPeerInfo> DeserializePeersCompact(byte[]? data, bool isIPv6 = false)
    {
        var peers = new List<SavedPeerInfo>();
        if (data == null || data.Length == 0)
            return peers;

        var peerSize = isIPv6 ? 18 : 6;
        var ipSize = isIPv6 ? 16 : 4;

        for (int i = 0; i + peerSize <= data.Length; i += peerSize)
        {
            var ipBytes = new byte[ipSize];
            Buffer.BlockCopy(data, i, ipBytes, 0, ipSize);
            var ip = new IPAddress(ipBytes);

            var port = (data[i + ipSize] << 8) | data[i + ipSize + 1];

            peers.Add(new SavedPeerInfo
            {
                IpAddress = ip.ToString(),
                Port = port,
                Source = "Resume",
                LastSeen = DateTime.UtcNow
            });
        }

        return peers;
    }

    private static bool IsIPv4(string ipAddress)
    {
        return IPAddress.TryParse(ipAddress, out var ip) &&
               ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    #endregion

    #region Helper Methods

    private static byte[]? GetBytesOrDefault(BDictionary dict, string key)
    {
        var str = dict.GetOrDefault<BString>(key);
        return str != null ? ((byte[])str) : null;
    }

    #endregion
}
