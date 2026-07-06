using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Merkle;

namespace vTorrent.Core.Engine;

/// <summary>
/// Creates v1, v2, or hybrid .torrent files from local files.
/// </summary>
public static class TorrentCreator
{
    public enum CreateMode { V1, V2, Hybrid }

    public readonly record struct TorrentCreateProgress(
        long BytesHashed, long TotalBytes, string CurrentFile);

    public static async Task<Torrent> CreateAsync(
        TorrentCreateOptions options,
        IProgress<TorrentCreateProgress>? progress = null,
        CancellationToken ct = default)
    {
        ValidateOptions(options);

        var pieceLength = options.PieceLength ?? AutoSelectPieceLength(options.FilePaths);

        var files = new List<TorrentFile>();
        var filePaths = new List<string>();
        long totalSize = 0;

        foreach (var path in options.FilePaths)
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
                throw new FileNotFoundException($"File not found: {path}");

            files.Add(new TorrentFile
            {
                Path = new[] { fileInfo.Name },
                Length = fileInfo.Length
            });
            filePaths.Add(path);
            totalSize += fileInfo.Length;
        }

        PieceHashes? v1Pieces = null;
        FileTree? fileTree = null;
        Dictionary<SHA256Hash, byte[]>? pieceLayers = null;

        if (options.Mode is CreateMode.V1 or CreateMode.Hybrid)
        {
            v1Pieces = await HashV1Async(filePaths, files, pieceLength, totalSize, progress, ct)
                .ConfigureAwait(false);
        }

        if (options.Mode is CreateMode.V2 or CreateMode.Hybrid)
        {
            (fileTree, pieceLayers) = await HashV2Async(filePaths, files, pieceLength, totalSize, progress, ct)
                .ConfigureAwait(false);
        }

        var info = new TorrentInfo
        {
            Name = options.Name,
            PieceLength = pieceLength,
            Pieces = v1Pieces,
            IsPrivate = options.IsPrivate,
            Source = options.Source,
            Files = files.AsReadOnly(),
            MetaVersion = fileTree is not null ? 2 : null,
            FileTreeV2 = fileTree,
        };

        // Resolve tracker tiers: TrackerTiers takes precedence over flat Trackers
        List<List<string>>? announceList = null;
        string announce = "";

        if (options.TrackerTiers is { Count: > 0 })
        {
            announce = options.TrackerTiers[0][0];
            announceList = options.TrackerTiers
                .Select(tier => tier.ToList())
                .ToList();
        }
        else if (options.Trackers is { Count: > 0 })
        {
            announce = options.Trackers[0];
            // Each tracker in its own tier
            announceList = options.Trackers
                .Select(t => new List<string> { t })
                .ToList();
        }

        return new Torrent
        {
            Announce = announce,
            AnnounceList = announceList?.Select(t => (IReadOnlyList<string>)t.AsReadOnly()).ToList().AsReadOnly(),
            Info = info,
            CreationDate = DateTimeOffset.UtcNow,
            CreatedBy = "vTorrent",
            Comment = options.Comment,
            PieceLayers = pieceLayers,
            UrlList = options.UrlSeeds,
            HttpSeeds = options.HttpSeeds,
        };
    }

    private static async Task<PieceHashes> HashV1Async(
        List<string> filePaths,
        List<TorrentFile> files,
        long pieceLength,
        long totalSize,
        IProgress<TorrentCreateProgress>? progress,
        CancellationToken ct)
    {
        var pieceCount = (int)((totalSize + pieceLength - 1) / pieceLength);
        var pieces = new PieceHashes(pieceCount);
        var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        int currentPiece = 0;
        long bytesInCurrentPiece = 0;
        long totalHashed = 0;

        foreach (var (path, file) in filePaths.Zip(files))
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536, useAsync: true);

            var buffer = new byte[65536];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                int offset = 0;
                while (offset < bytesRead)
                {
                    var remaining = (int)(pieceLength - bytesInCurrentPiece);
                    var toHash = Math.Min(remaining, bytesRead - offset);

                    sha1.AppendData(buffer, offset, toHash);
                    bytesInCurrentPiece += toHash;
                    offset += toHash;
                    totalHashed += toHash;

                    if (bytesInCurrentPiece == pieceLength)
                    {
                        var hash = sha1.GetHashAndReset();
                        pieces.SetPieceHash(currentPiece, hash);
                        currentPiece++;
                        bytesInCurrentPiece = 0;
                    }
                }

                progress?.Report(new TorrentCreateProgress(totalHashed, totalSize, file.GetFullPath()));
            }
        }

        if (bytesInCurrentPiece > 0 && currentPiece < pieceCount)
        {
            var hash = sha1.GetHashAndReset();
            pieces.SetPieceHash(currentPiece, hash);
        }

        sha1.Dispose();
        return pieces;
    }

    private static async Task<(FileTree, Dictionary<SHA256Hash, byte[]>)> HashV2Async(
        List<string> filePaths,
        List<TorrentFile> files,
        long pieceLength,
        long totalSize,
        IProgress<TorrentCreateProgress>? progress,
        CancellationToken ct)
    {
        var blockSize = MerkleHelpers.BlockSize;
        var pieceLayers = new Dictionary<SHA256Hash, byte[]>();
        var fileTreeChildren = new SortedDictionary<string, FileTreeNode>(StringComparer.Ordinal);
        long totalHashed = 0;

        foreach (var (path, file) in filePaths.Zip(files))
        {
            if (file.Length == 0)
            {
                fileTreeChildren[file.Path.Last()] = FileTreeNode.File(
                    file.Path.Last(), new FileTreeEntry(0, null));
                continue;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536, useAsync: true);

            var blockHashes = new List<SHA256Hash>();
            var buffer = new byte[blockSize];

            while (true)
            {
                var bytesRead = await ReadExactlyOrLessAsync(stream, buffer, ct).ConfigureAwait(false);
                if (bytesRead == 0) break;

                blockHashes.Add(new SHA256Hash(SHA256.HashData(buffer.AsSpan(0, bytesRead))));
                totalHashed += bytesRead;
                progress?.Report(new TorrentCreateProgress(totalHashed, totalSize, file.GetFullPath()));
            }

            var tree = MerkleTree.FromLeaves(blockHashes.ToArray());
            var piecesRoot = tree.Root;

            var blocksPerPiece = (int)(pieceLength / blockSize);
            var pieceLayerHashes = ExtractPieceLayer(tree, blocksPerPiece, blockHashes.Count);
            pieceLayers[piecesRoot] = PieceLayerToBytes(pieceLayerHashes);

            fileTreeChildren[file.Path.Last()] = FileTreeNode.File(
                file.Path.Last(), new FileTreeEntry(file.Length, piecesRoot));
        }

        var root = FileTreeNode.Directory("", fileTreeChildren);
        var fileTree = new FileTree(root);

        return (fileTree, pieceLayers);
    }

    private static List<SHA256Hash> ExtractPieceLayer(MerkleTree tree, int blocksPerPiece, int actualBlocks)
    {
        if (blocksPerPiece <= 1)
        {
            var hashes = new List<SHA256Hash>();
            var firstLeaf = MerkleHelpers.FirstLeafIndex(tree.LeafCount);
            for (int i = 0; i < actualBlocks; i++)
                hashes.Add(tree.GetNode(firstLeaf + i));
            return hashes;
        }

        var levelsUp = System.Numerics.BitOperations.Log2((uint)blocksPerPiece);
        var pieceCount = (actualBlocks + blocksPerPiece - 1) / blocksPerPiece;
        var result = new List<SHA256Hash>();

        var firstLeafIdx = MerkleHelpers.FirstLeafIndex(tree.LeafCount);
        for (int p = 0; p < pieceCount; p++)
        {
            var leafIdx = firstLeafIdx + p * blocksPerPiece;
            var nodeIdx = leafIdx;
            for (int l = 0; l < levelsUp; l++)
                nodeIdx = MerkleHelpers.Parent(nodeIdx);
            result.Add(tree.GetNode(nodeIdx));
        }

        return result;
    }

    private static byte[] PieceLayerToBytes(List<SHA256Hash> hashes)
    {
        var result = new byte[hashes.Count * SHA256Hash.Size];
        for (int i = 0; i < hashes.Count; i++)
            hashes[i].AsSpan().CopyTo(result.AsSpan(i * SHA256Hash.Size));
        return result;
    }

    private static async Task<int> ReadExactlyOrLessAsync(
        Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead), ct).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    private static void ValidateOptions(TorrentCreateOptions options)
    {
        if (string.IsNullOrEmpty(options.Name))
            throw new ArgumentException("Name is required");
        if (options.FilePaths is null || options.FilePaths.Count == 0)
            throw new ArgumentException("At least one file path is required");

        if (options.PieceLength.HasValue)
        {
            var pl = options.PieceLength.Value;
            if (options.Mode is CreateMode.V2 or CreateMode.Hybrid)
            {
                if (pl < 16384)
                    throw new ArgumentException("v2 piece length must be >= 16 KiB");
                if ((pl & (pl - 1)) != 0)
                    throw new ArgumentException("v2 piece length must be a power of 2");
            }
            if (pl <= 0)
                throw new ArgumentException("Piece length must be positive");
        }
    }

    private static long AutoSelectPieceLength(IReadOnlyList<string> filePaths)
    {
        long totalSize = filePaths.Sum(p => new FileInfo(p).Length);

        // libtorrent 2.0.11 algorithm: target_list_size = sqrt(total_size) * 2
        // Precomputed thresholds from create_torrent.cpp lines 419-429
        ReadOnlySpan<long> sizeTable = stackalloc long[]
        {
                   2_684_355L, //  16 KiB
                  10_737_418L, //  32 KiB
                  42_949_673L, //  64 KiB
                 171_798_692L, // 128 KiB
                 687_194_767L, // 256 KiB
               2_748_779_069L, // 512 KiB
              10_995_116_278L, //   1 MiB
              43_980_465_111L, //   2 MiB
             175_921_860_444L, //   4 MiB
             703_687_441_777L, //   8 MiB
        };

        int i = 0;
        foreach (var threshold in sizeTable)
        {
            if (totalSize < threshold) break;
            i++;
        }

        return 16384L << i;
    }
}

/// <summary>
/// Options for creating a .torrent file.
/// </summary>
public sealed record TorrentCreateOptions
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> FilePaths { get; init; }
    public TorrentCreator.CreateMode Mode { get; init; } = TorrentCreator.CreateMode.Hybrid;
    public long? PieceLength { get; init; }

    /// <summary>Flat tracker list (each becomes its own tier). Use <see cref="TrackerTiers"/> for explicit tiers.</summary>
    public IReadOnlyList<string>? Trackers { get; init; }

    /// <summary>Multi-tier tracker list. Takes precedence over <see cref="Trackers"/> if set.</summary>
    public IReadOnlyList<IReadOnlyList<string>>? TrackerTiers { get; init; }

    public bool IsPrivate { get; init; }
    public string? Source { get; init; }
    public string? Comment { get; init; }

    /// <summary>BEP 19 URL seeds (GetRight-style).</summary>
    public IReadOnlyList<string>? UrlSeeds { get; init; }

    /// <summary>BEP 17 HTTP seeds.</summary>
    public IReadOnlyList<string>? HttpSeeds { get; init; }
}
