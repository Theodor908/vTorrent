using System;
using System.Numerics;

namespace vTorrent.Core.Merkle;

/// <summary>
/// Pure arithmetic helpers for navigating a flat-array binary merkle tree.
/// Root is at index 0. Children of node i are at 2i+1 (left) and 2i+2 (right).
/// Mirrors libtorrent's merkle.hpp navigation functions.
/// </summary>
public static class MerkleHelpers
{
    public const int BlockSize = 16384; // 16 KiB — BEP 52 leaf hash block size

    public static int Parent(int index) => (index - 1) / 2;

    public static int LeftChild(int index) => 2 * index + 1;

    public static int RightChild(int index) => 2 * index + 2;

    /// <summary>XOR with 1 flips LSB: odd↔even sibling pairs (1↔2, 3↔4, 5↔6).</summary>
    public static int Sibling(int index) => ((index - 1) ^ 1) + 1;

    /// <summary>Full binary tree: 2*leaves - 1 nodes.</summary>
    public static int NodeCount(int leafCount) => leafCount <= 1 ? 1 : 2 * leafCount - 1;

    /// <summary>First leaf is at index leafCount - 1 in a full binary tree.</summary>
    public static int FirstLeafIndex(int leafCount) => leafCount <= 1 ? 0 : leafCount - 1;

    /// <summary>Round up to the next power of two (or 1 if value ≤ 1).</summary>
    public static int NextPowerOfTwo(int value)
    {
        if (value <= 1) return 1;
        return (int)BitOperations.RoundUpToPowerOf2((uint)value);
    }

    /// <summary>
    /// Number of leaves for a file of given size, padded to power of two.
    /// Each leaf covers blockSize bytes.
    /// </summary>
    public static int LeafCountForFile(long fileSize, int blockSize = BlockSize)
    {
        if (fileSize <= 0) return 0;
        var blocks = (int)((fileSize + blockSize - 1) / blockSize);
        return NextPowerOfTwo(blocks);
    }
}
