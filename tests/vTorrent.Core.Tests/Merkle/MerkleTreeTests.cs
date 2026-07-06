using FluentAssertions;
using System.Security.Cryptography;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Merkle;
using Xunit;

namespace vTorrent.Tests.Unit.Core.Merkle;

public class MerkleTreeTests
{
    private static SHA256Hash HashBlock(byte[] data)
    {
        return new SHA256Hash(SHA256.HashData(data));
    }

    private static SHA256Hash CreateHash(byte fillByte)
    {
        var bytes = new byte[32];
        Array.Fill(bytes, fillByte);
        return new SHA256Hash(bytes);
    }

    private static SHA256Hash HashPair(SHA256Hash left, SHA256Hash right)
    {
        Span<byte> combined = stackalloc byte[64];
        left.AsSpan().CopyTo(combined);
        right.AsSpan().CopyTo(combined[32..]);
        return new SHA256Hash(SHA256.HashData(combined));
    }

    [Fact]
    public void FromLeaves_SingleLeaf_RootEqualsLeaf()
    {
        var leaf = HashBlock(new byte[] { 1, 2, 3 });
        var tree = MerkleTree.FromLeaves(new[] { leaf });

        tree.Root.Should().Be(leaf);
        tree.LeafCount.Should().Be(1);
        tree.NodeCount.Should().Be(1);
    }

    [Fact]
    public void FromLeaves_TwoLeaves_RootIsHashOfPair()
    {
        var left = HashBlock(new byte[] { 1 });
        var right = HashBlock(new byte[] { 2 });
        var expectedRoot = HashPair(left, right);

        var tree = MerkleTree.FromLeaves(new[] { left, right });

        tree.Root.Should().Be(expectedRoot);
        tree.LeafCount.Should().Be(2);
        tree.NodeCount.Should().Be(3);
    }

    [Fact]
    public void FromLeaves_FourLeaves_ComputesCorrectRoot()
    {
        var leaves = new SHA256Hash[4];
        for (int i = 0; i < 4; i++)
            leaves[i] = HashBlock(new byte[] { (byte)i });

        var tree = MerkleTree.FromLeaves(leaves);

        // Manual: H(H(L0,L1), H(L2,L3))
        var h01 = HashPair(leaves[0], leaves[1]);
        var h23 = HashPair(leaves[2], leaves[3]);
        var expectedRoot = HashPair(h01, h23);

        tree.Root.Should().Be(expectedRoot);
        tree.LeafCount.Should().Be(4);
    }

    [Fact]
    public void FromLeaves_ThreeLeaves_PadsToFourWithZeros()
    {
        var leaves = new SHA256Hash[3];
        for (int i = 0; i < 3; i++)
            leaves[i] = HashBlock(new byte[] { (byte)(i + 10) });

        var tree = MerkleTree.FromLeaves(leaves);

        // 3 leaves → padded to 4, 4th leaf is zero hash
        tree.LeafCount.Should().Be(4);
        tree.NodeCount.Should().Be(7);
    }

    [Fact]
    public void ValidateBlock_CorrectHash_ReturnsTrue()
    {
        var leaves = new SHA256Hash[4];
        for (int i = 0; i < 4; i++)
            leaves[i] = HashBlock(new byte[] { (byte)i });

        var tree = MerkleTree.FromLeaves(leaves);

        tree.ValidateBlock(0, leaves[0]).Should().BeTrue();
        tree.ValidateBlock(1, leaves[1]).Should().BeTrue();
        tree.ValidateBlock(3, leaves[3]).Should().BeTrue();
    }

    [Fact]
    public void ValidateBlock_WrongHash_ReturnsFalse()
    {
        var leaves = new SHA256Hash[2];
        leaves[0] = HashBlock(new byte[] { 1 });
        leaves[1] = HashBlock(new byte[] { 2 });

        var tree = MerkleTree.FromLeaves(leaves);

        var wrongHash = HashBlock(new byte[] { 99 });
        tree.ValidateBlock(0, wrongHash).Should().BeFalse();
    }

    [Fact]
    public void GetNode_ReturnsCorrectHashes()
    {
        var left = HashBlock(new byte[] { 1 });
        var right = HashBlock(new byte[] { 2 });
        var tree = MerkleTree.FromLeaves(new[] { left, right });

        tree.GetNode(1).Should().Be(left);   // left leaf at index 1
        tree.GetNode(2).Should().Be(right);  // right leaf at index 2
        tree.GetNode(0).Should().Be(tree.Root); // root at index 0
    }

    [Fact]
    public void GetUncleHashes_ReturnsProofPath()
    {
        var leaves = new SHA256Hash[4];
        for (int i = 0; i < 4; i++)
            leaves[i] = HashBlock(new byte[] { (byte)i });

        var tree = MerkleTree.FromLeaves(leaves);

        // Uncle hashes for leaf 0 (index 3): sibling is index 4, then uncle is index 2
        var uncles = tree.GetUncleHashes(3);
        uncles.Should().HaveCount(2);
        uncles[0].Should().Be(leaves[1]);  // sibling of leaf 0
        uncles[1].Should().Be(HashPair(leaves[2], leaves[3])); // uncle at level 1
    }

    [Fact]
    public void ValidateWithProof_CorrectProof_ReturnsTrue()
    {
        var leaves = new SHA256Hash[4];
        for (int i = 0; i < 4; i++)
            leaves[i] = HashBlock(new byte[] { (byte)i });

        var tree = MerkleTree.FromLeaves(leaves);
        var uncles = tree.GetUncleHashes(3); // uncles for leaf 0

        // Verify leaf 0 with its proof against the known root
        MerkleTree.ValidateWithProof(leaves[0], 3, uncles, tree.Root)
            .Should().BeTrue();
    }

    [Fact]
    public void ValidateWithProof_WrongHash_ReturnsFalse()
    {
        var leaves = new SHA256Hash[4];
        for (int i = 0; i < 4; i++)
            leaves[i] = HashBlock(new byte[] { (byte)i });

        var tree = MerkleTree.FromLeaves(leaves);
        var uncles = tree.GetUncleHashes(3);
        var wrongHash = HashBlock(new byte[] { 99 });

        MerkleTree.ValidateWithProof(wrongHash, 3, uncles, tree.Root)
            .Should().BeFalse();
    }

    // --- FromPieceLayer tests ---

    [Fact]
    public void FromPieceLayer_SinglePiece_128KiB_CorrectGeometry()
    {
        var blocksPerPiece = 8;
        var pieceHash = CreateHash(0xAA);

        var tree = MerkleTree.FromPieceLayer(new[] { pieceHash }, blocksPerPiece, expectedRoot: null);

        tree.LeafCount.Should().Be(8);
        tree.NodeCount.Should().Be(15);
    }

    [Fact]
    public void FromPieceLayer_PieceHashAtCorrectLayer()
    {
        var blocksPerPiece = 4;
        var piece0 = CreateHash(0x01);
        var piece1 = CreateHash(0x02);

        var tree = MerkleTree.FromPieceLayer(new[] { piece0, piece1 }, blocksPerPiece, expectedRoot: null);

        tree.LeafCount.Should().Be(8);
        // Layer 2 (piece layer): indices 1,2
        tree.GetNode(1).Should().Be(piece0);
        tree.GetNode(2).Should().Be(piece1);
    }

    [Fact]
    public void FromPieceLayer_16KiB_PieceEqualsBlock()
    {
        var blocksPerPiece = 1;
        var piece0 = CreateHash(0x10);
        var piece1 = CreateHash(0x20);

        var tree = MerkleTree.FromPieceLayer(new[] { piece0, piece1 }, blocksPerPiece, expectedRoot: null);

        tree.LeafCount.Should().Be(2);
        var firstLeaf = MerkleHelpers.FirstLeafIndex(2);
        tree.GetNode(firstLeaf).Should().Be(piece0);
        tree.GetNode(firstLeaf + 1).Should().Be(piece1);
    }

    [Fact]
    public void FromPieceLayer_SinglePiece_2Blocks_RootIsPieceHash()
    {
        var blocksPerPiece = 2;
        var pieceHash = CreateHash(0xFF);

        var tree = MerkleTree.FromPieceLayer(new[] { pieceHash }, blocksPerPiece, expectedRoot: null);

        tree.Root.Should().Be(pieceHash);
    }

    [Fact]
    public void FromPieceLayer_ExpectedRootMismatch_Throws()
    {
        var pieceHash = CreateHash(0x01);
        var wrongRoot = CreateHash(0xFF);

        var act = () => MerkleTree.FromPieceLayer(new[] { pieceHash }, blocksPerPiece: 2, expectedRoot: wrongRoot);

        act.Should().Throw<System.IO.InvalidDataException>();
    }

    [Fact]
    public void FromPieceLayer_ExpectedRootMatch_Succeeds()
    {
        var pieceHash = CreateHash(0xFF);
        var tree = MerkleTree.FromPieceLayer(new[] { pieceHash }, blocksPerPiece: 2, expectedRoot: pieceHash);

        tree.Root.Should().Be(pieceHash);
    }
}
