using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Bencode.Torrents;

namespace vTorrent.Core.Merkle;

/// <summary>
/// Persists MerkleTree instances to binary .tree files.
/// One file per torrent, stored in the torrent's data directory.
///
/// Format:
///   [4B] magic "vT2\0"
///   [4B] format version (1)
///   [4B] file count
///   Per file:
///     [4B] leaf count (padded)
///     [4B] node count
///     [N * 32B] tree nodes (flat array)
///     [ceil(leafCount/8) bytes] verified bitfield
/// </summary>
public sealed class MerkleTreeStore
{
    private static readonly byte[] Magic = "vT2\0"u8.ToArray();
    private const int FormatVersion = 1;

    private readonly string _directory;

    public MerkleTreeStore(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public async Task SaveAsync(
        string infoHash,
        IReadOnlyList<MerkleTree> trees,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_directory);
        var filePath = GetFilePath(infoHash);

        await using var stream = new FileStream(
            filePath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 65536, useAsync: true);

        // Header
        await stream.WriteAsync(Magic, ct).ConfigureAwait(false);
        await WriteInt32Async(stream, FormatVersion, ct).ConfigureAwait(false);
        await WriteInt32Async(stream, trees.Count, ct).ConfigureAwait(false);

        // Per file
        foreach (var tree in trees)
        {
            await WriteInt32Async(stream, tree.LeafCount, ct).ConfigureAwait(false);
            await WriteInt32Async(stream, tree.NodeCount, ct).ConfigureAwait(false);

            // Write all nodes
            for (int i = 0; i < tree.NodeCount; i++)
            {
                var nodeBytes = tree.GetNode(i).Bytes;
                await stream.WriteAsync(nodeBytes, ct).ConfigureAwait(false);
            }

            // Write verified bitfield
            var bitfieldBytes = new byte[(tree.LeafCount + 7) / 8];
            for (int i = 0; i < tree.LeafCount; i++)
            {
                if (tree.IsBlockVerified(i))
                    bitfieldBytes[i / 8] |= (byte)(1 << (i % 8));
            }
            await stream.WriteAsync(bitfieldBytes, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MerkleTree>?> LoadAsync(
        string infoHash,
        IReadOnlyList<SHA256Hash> expectedRoots,
        CancellationToken ct = default)
    {
        var filePath = GetFilePath(infoHash);
        if (!File.Exists(filePath))
            return null;

        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, useAsync: true);

        // Read and validate header
        var magic = new byte[4];
        await stream.ReadExactlyAsync(magic, ct).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Invalid .tree file magic");

        var version = await ReadInt32Async(stream, ct).ConfigureAwait(false);
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported .tree format version {version}");

        var fileCount = await ReadInt32Async(stream, ct).ConfigureAwait(false);
        if (fileCount != expectedRoots.Count)
            throw new InvalidDataException(
                $"Tree file has {fileCount} files but expected {expectedRoots.Count}");

        var trees = new List<MerkleTree>(fileCount);

        for (int f = 0; f < fileCount; f++)
        {
            var leafCount = await ReadInt32Async(stream, ct).ConfigureAwait(false);
            var nodeCount = await ReadInt32Async(stream, ct).ConfigureAwait(false);

            // Read nodes
            var nodes = new SHA256Hash[nodeCount];
            var nodeBuffer = new byte[SHA256Hash.Size];
            for (int i = 0; i < nodeCount; i++)
            {
                await stream.ReadExactlyAsync(nodeBuffer, ct).ConfigureAwait(false);
                nodes[i] = new SHA256Hash(nodeBuffer);
            }

            // Validate root
            if (nodes[0] != expectedRoots[f])
                throw new InvalidDataException(
                    $"Tree root mismatch for file {f}: expected {expectedRoots[f]}, got {nodes[0]}");

            // Read verified bitfield
            var bitfieldByteCount = (leafCount + 7) / 8;
            var bitfieldBytes = new byte[bitfieldByteCount];
            await stream.ReadExactlyAsync(bitfieldBytes, ct).ConfigureAwait(false);

            var tree = MerkleTree.FromNodes(nodes, leafCount);

            // Restore verified state
            for (int i = 0; i < leafCount; i++)
            {
                if ((bitfieldBytes[i / 8] & (1 << (i % 8))) != 0)
                    tree.SetBlockVerified(i);
            }

            trees.Add(tree);
        }

        return trees;
    }

    private string GetFilePath(string infoHash) => Path.Combine(_directory, $"{infoHash}.tree");

    private static async Task WriteInt32Async(Stream stream, int value, CancellationToken ct)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadInt32Async(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }
}
