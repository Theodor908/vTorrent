using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using vTorrent.Bencode.Torrents;

namespace vTorrent.Core.Merkle;

/// <summary>
/// Per-file binary merkle hash tree for BEP 52.
/// Flat array layout: root at index 0, children of node i at 2i+1 and 2i+2.
/// Leaf nodes hash 16 KiB blocks with SHA-256.
/// Mirrors libtorrent's merkle_tree class.
/// </summary>
public sealed class MerkleTree
{
    private readonly SHA256Hash[] _nodes;
    private readonly BitArray _verified;
    private readonly int _leafCount; // padded to power of 2

    public SHA256Hash Root => _nodes[0];
    public int LeafCount => _leafCount;
    public int NodeCount => _nodes.Length;

    private MerkleTree(SHA256Hash[] nodes, int leafCount)
    {
        _nodes = nodes;
        _leafCount = leafCount;
        _verified = new BitArray(leafCount);
    }

    /// <summary>
    /// Build a complete tree from leaf hashes. Pads to next power of two with zero hashes.
    /// </summary>
    public static MerkleTree FromLeaves(ReadOnlySpan<SHA256Hash> leaves)
    {
        if (leaves.Length == 0)
            throw new ArgumentException("Must have at least one leaf");

        var paddedCount = MerkleHelpers.NextPowerOfTwo(leaves.Length);
        var nodeCount = MerkleHelpers.NodeCount(paddedCount);
        var nodes = new SHA256Hash[nodeCount];

        // Place leaves at bottom layer
        var firstLeaf = MerkleHelpers.FirstLeafIndex(paddedCount);
        for (int i = 0; i < leaves.Length; i++)
            nodes[firstLeaf + i] = leaves[i];
        // Remaining leaves are default (zero hash) — correct per BEP 52

        // Build tree bottom-up
        FillTree(nodes, paddedCount);

        return new MerkleTree(nodes, paddedCount);
    }

    /// <summary>
    /// Reconstruct tree from pre-computed node array (for loading from .tree files).
    /// </summary>
    public static MerkleTree FromNodes(SHA256Hash[] nodes, int leafCount)
    {
        if (nodes.Length != MerkleHelpers.NodeCount(leafCount))
            throw new ArgumentException("Node count doesn't match leaf count");
        return new MerkleTree((SHA256Hash[])nodes.Clone(), leafCount);
    }

    /// <summary>
    /// Build a tree from piece layer hashes (one hash per piece).
    /// Used when loading from .torrent file where only piece-level hashes are available.
    /// Block-level leaves are left as zero hashes until blocks arrive from peers.
    /// </summary>
    /// <param name="pieceHashes">Concatenated piece-layer hashes from the .torrent.</param>
    /// <param name="blocksPerPiece">Number of 16 KiB blocks per piece (piece_length / 16384).</param>
    /// <param name="expectedRoot">If not null, validate the computed root matches. Throws InvalidDataException on mismatch.</param>
    public static MerkleTree FromPieceLayer(
        ReadOnlySpan<SHA256Hash> pieceHashes,
        int blocksPerPiece,
        SHA256Hash? expectedRoot = null)
    {
        if (pieceHashes.Length == 0)
            throw new ArgumentException("Must have at least one piece hash");

        if (blocksPerPiece <= 0)
            throw new ArgumentException("Blocks per piece must be positive");

        // Special case: piece_length == 16 KiB → piece hashes ARE block hashes (leaf layer)
        if (blocksPerPiece == 1)
        {
            var leafTree = FromLeaves(pieceHashes);
            if (expectedRoot.HasValue && leafTree.Root != expectedRoot.Value)
                throw new System.IO.InvalidDataException(
                    $"Merkle tree root mismatch: expected {expectedRoot.Value}, got {leafTree.Root}");
            return leafTree;
        }

        // Total blocks across all pieces
        var totalBlocks = pieceHashes.Length * blocksPerPiece;
        var paddedLeafCount = MerkleHelpers.NextPowerOfTwo(totalBlocks);
        var nodeCount = MerkleHelpers.NodeCount(paddedLeafCount);
        var nodes = new SHA256Hash[nodeCount];

        var firstLeaf = MerkleHelpers.FirstLeafIndex(paddedLeafCount);
        var layerHeight = (int)Math.Log2(blocksPerPiece);

        // Place piece hashes at the correct intermediate layer.
        // Walk up layerHeight levels from the first leaf of each piece's block range.
        for (int p = 0; p < pieceHashes.Length; p++)
        {
            var leafIdx = firstLeaf + p * blocksPerPiece;
            var pieceNodeIdx = leafIdx;
            for (int level = 0; level < layerHeight; level++)
                pieceNodeIdx = MerkleHelpers.Parent(pieceNodeIdx);

            nodes[pieceNodeIdx] = pieceHashes[p];
        }

        // Build tree above the piece layer up to root.
        var firstPieceNode = firstLeaf;
        for (int level = 0; level < layerHeight; level++)
            firstPieceNode = MerkleHelpers.Parent(firstPieceNode);

        // CRITICAL: If piece layer IS the root (single piece), skip the fill loop.
        // Parent(0) = (0-1)/2 = 0 in C# (truncation toward zero), which would
        // execute the loop once and overwrite the root with HashPair(zero, zero).
        if (firstPieceNode > 0)
        {
            for (int i = MerkleHelpers.Parent(firstPieceNode); i >= 0; i--)
            {
                var left = nodes[MerkleHelpers.LeftChild(i)];
                var right = nodes[MerkleHelpers.RightChild(i)];
                nodes[i] = HashPair(left, right);
            }
        }

        var tree = new MerkleTree(nodes, paddedLeafCount);

        if (expectedRoot.HasValue && tree.Root != expectedRoot.Value)
            throw new System.IO.InvalidDataException(
                $"Merkle tree root mismatch: expected {expectedRoot.Value}, got {tree.Root}");

        return tree;
    }

    /// <summary>
    /// Validate a block hash against the expected leaf node.
    /// </summary>
    public bool ValidateBlock(int blockIndex, SHA256Hash hash)
    {
        if (blockIndex < 0 || blockIndex >= _leafCount) return false;

        var leafIndex = MerkleHelpers.FirstLeafIndex(_leafCount) + blockIndex;
        return _nodes[leafIndex] == hash;
    }

    /// <summary>
    /// Get the hash at a given tree node index.
    /// </summary>
    public SHA256Hash GetNode(int index)
    {
        if (index < 0 || index >= _nodes.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _nodes[index];
    }

    /// <summary>
    /// Set a node hash (used when receiving validated hashes from peers).
    /// </summary>
    public void SetNode(int index, SHA256Hash hash)
    {
        if (index < 0 || index >= _nodes.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        _nodes[index] = hash;
    }

    /// <summary>
    /// Compute uncle (sibling) hashes from a node up to the root.
    /// These form the merkle proof for the node's hash.
    /// </summary>
    public IReadOnlyList<SHA256Hash> GetUncleHashes(int nodeIndex)
    {
        if (nodeIndex < 0 || nodeIndex >= _nodes.Length)
            throw new ArgumentOutOfRangeException(nameof(nodeIndex));

        var uncles = new List<SHA256Hash>();
        var current = nodeIndex;

        while (current > 0) // stop at root
        {
            uncles.Add(_nodes[MerkleHelpers.Sibling(current)]);
            current = MerkleHelpers.Parent(current);
        }

        return uncles;
    }

    /// <summary>
    /// Verify a hash at a given node index using uncle hashes (merkle proof).
    /// Recomputes the path from the node to the root and checks against expectedRoot.
    /// </summary>
    public static bool ValidateWithProof(
        SHA256Hash nodeHash,
        int nodeIndex,
        IReadOnlyList<SHA256Hash> uncleHashes,
        SHA256Hash expectedRoot)
    {
        var current = nodeHash;
        var idx = nodeIndex;

        for (int i = 0; i < uncleHashes.Count; i++)
        {
            var uncle = uncleHashes[i];
            var isLeftChild = (idx % 2 == 1); // odd index = left child

            current = isLeftChild
                ? HashPair(current, uncle)
                : HashPair(uncle, current);

            idx = MerkleHelpers.Parent(idx);
        }

        return current == expectedRoot;
    }

    /// <summary>Mark a block as verified.</summary>
    public void SetBlockVerified(int blockIndex)
    {
        if (blockIndex >= 0 && blockIndex < _leafCount)
            _verified[blockIndex] = true;
    }

    /// <summary>Check if a block has been verified.</summary>
    public bool IsBlockVerified(int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= _leafCount) return false;
        return _verified[blockIndex];
    }

    // --- Internal helpers ---

    private static void FillTree(SHA256Hash[] nodes, int leafCount)
    {
        if (leafCount <= 1) return;

        // Start from the layer above leaves, work up to root
        var firstLeaf = MerkleHelpers.FirstLeafIndex(leafCount);
        for (int i = firstLeaf - 1; i >= 0; i--)
        {
            var left = nodes[MerkleHelpers.LeftChild(i)];
            var right = nodes[MerkleHelpers.RightChild(i)];
            nodes[i] = HashPair(left, right);
        }
    }

    private static SHA256Hash HashPair(SHA256Hash left, SHA256Hash right)
    {
        Span<byte> combined = stackalloc byte[64];
        left.AsSpan().CopyTo(combined);
        right.AsSpan().CopyTo(combined[32..]);
        return new SHA256Hash(SHA256.HashData(combined));
    }
}
