using FluentAssertions;
using vTorrent.Core.Merkle;
using Xunit;

namespace vTorrent.Tests.Unit.Core.Merkle;

public class MerkleHelpersTests
{
    // Tree layout (4 leaves):
    //        0
    //      /   \
    //     1     2
    //    / \   / \
    //   3   4 5   6

    [Theory]
    [InlineData(3, 1)]  // leaf 3 → parent 1
    [InlineData(4, 1)]  // leaf 4 → parent 1
    [InlineData(5, 2)]  // leaf 5 → parent 2
    [InlineData(6, 2)]  // leaf 6 → parent 2
    [InlineData(1, 0)]  // internal 1 → root 0
    [InlineData(2, 0)]  // internal 2 → root 0
    public void Parent_ReturnsCorrectIndex(int child, int expectedParent)
    {
        MerkleHelpers.Parent(child).Should().Be(expectedParent);
    }

    [Theory]
    [InlineData(1, 3)]  // node 1 → left child 3
    [InlineData(2, 5)]  // node 2 → left child 5
    [InlineData(0, 1)]  // root 0 → left child 1
    public void LeftChild_ReturnsCorrectIndex(int node, int expectedLeft)
    {
        MerkleHelpers.LeftChild(node).Should().Be(expectedLeft);
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 6)]
    [InlineData(0, 2)]
    public void RightChild_ReturnsCorrectIndex(int node, int expectedRight)
    {
        MerkleHelpers.RightChild(node).Should().Be(expectedRight);
    }

    [Theory]
    [InlineData(3, 4)]  // siblings
    [InlineData(4, 3)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(5, 6)]
    public void Sibling_ReturnsCorrectIndex(int node, int expectedSibling)
    {
        MerkleHelpers.Sibling(node).Should().Be(expectedSibling);
    }

    [Theory]
    [InlineData(1, 1)]   // 1 leaf → 1 node (root only)
    [InlineData(2, 3)]   // 2 leaves → 3 nodes
    [InlineData(4, 7)]   // 4 leaves → 7 nodes
    [InlineData(8, 15)]  // 8 leaves → 15 nodes
    public void NodeCount_ForLeafCount(int leaves, int expectedNodes)
    {
        MerkleHelpers.NodeCount(leaves).Should().Be(expectedNodes);
    }

    [Theory]
    [InlineData(1, 0)]  // 1 leaf: first leaf at index 0
    [InlineData(2, 1)]  // 2 leaves: first leaf at index 1
    [InlineData(4, 3)]  // 4 leaves: first leaf at index 3
    [InlineData(8, 7)]  // 8 leaves: first leaf at index 7
    public void FirstLeafIndex_ReturnsCorrectOffset(int leafCount, int expected)
    {
        MerkleHelpers.FirstLeafIndex(leafCount).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 1)]   // 0 → 1
    [InlineData(1, 1)]   // 1 → 1
    [InlineData(2, 2)]   // already power of 2
    [InlineData(3, 4)]
    [InlineData(5, 8)]
    [InlineData(7, 8)]
    [InlineData(9, 16)]
    public void NextPowerOfTwo_ReturnsCorrectValue(int value, int expected)
    {
        MerkleHelpers.NextPowerOfTwo(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(100, 16384, 1)]    // 100 bytes → 1 block → padded to 1 leaf
    [InlineData(16384, 16384, 1)]  // exactly 1 block
    [InlineData(32768, 16384, 2)]  // exactly 2 blocks
    [InlineData(50000, 16384, 4)]  // 4 blocks (ceil(50000/16384)=4, pad to 4)
    public void LeafCountForFile_ReturnsCorrectPaddedCount(long fileSize, int blockSize, int expected)
    {
        MerkleHelpers.LeafCountForFile(fileSize, blockSize).Should().Be(expected);
    }
}
