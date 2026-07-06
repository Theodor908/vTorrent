using FluentAssertions;
using System.Security.Cryptography;
using vTorrent.Bencode.Torrents;
using vTorrent.Core;
using vTorrent.Core.Merkle;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Utilities;
using Xunit;
using vTorrent.Core.Download;

namespace vTorrent.Tests.Unit.Core;

public class HashPickerTests
{
    private static SHA256Hash HashBlock(byte value)
        => new(SHA256.HashData(new[] { value }));

    private static MerkleTree CreateTree(int leafCount)
    {
        var leaves = new SHA256Hash[leafCount];
        for (int i = 0; i < leafCount; i++)
            leaves[i] = HashBlock((byte)i);
        return MerkleTree.FromLeaves(leaves);
    }

    [Fact]
    public void PickHashRequest_NeedHashes_ReturnsRequest()
    {
        var tree = CreateTree(4);
        var picker = new HashPicker(
            new[] { tree.Root },
            filePieceOffsets: new[] { 0 },
            blocksPerPiece: 1,
            totalPieces: 4);

        var peerBitfield = new Bitfield(4);
        peerBitfield.SetPiece(0);

        var request = picker.PickHashRequest(peerBitfield);

        request.Should().NotBeNull();
        request!.Value.PiecesRoot.Should().Be(tree.Root);
        request.Value.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void PickHashRequest_AlreadyHaveHashes_ReturnsNull()
    {
        var tree = CreateTree(4);
        var picker = new HashPicker(
            new[] { tree.Root },
            filePieceOffsets: new[] { 0 },
            blocksPerPiece: 1,
            totalPieces: 4);

        picker.MarkHashesReceived(tree.Root, 0, 4);

        var peerBitfield = new Bitfield(4);
        peerBitfield.SetPiece(0);

        var request = picker.PickHashRequest(peerBitfield);
        request.Should().BeNull();
    }

    [Fact]
    public void HasBlockHashes_AfterReceiving_ReturnsTrue()
    {
        var tree = CreateTree(4);
        var picker = new HashPicker(
            new[] { tree.Root },
            filePieceOffsets: new[] { 0 },
            blocksPerPiece: 1,
            totalPieces: 4);

        picker.HasBlockHashes(0).Should().BeFalse();

        picker.MarkHashesReceived(tree.Root, 0, 4);

        picker.HasBlockHashes(0).Should().BeTrue();
        picker.HasBlockHashes(3).Should().BeTrue();
    }

    [Fact]
    public void PickHashRequest_PendingRequest_DoesNotDuplicate()
    {
        var tree = CreateTree(4);
        var picker = new HashPicker(
            new[] { tree.Root },
            filePieceOffsets: new[] { 0 },
            blocksPerPiece: 1,
            totalPieces: 4);

        var bitfield = new Bitfield(4);
        bitfield.SetPiece(0);

        var first = picker.PickHashRequest(bitfield);
        first.Should().NotBeNull();

        var second = picker.PickHashRequest(bitfield);
        if (second.HasValue)
            second.Value.Index.Should().NotBe(first!.Value.Index);
    }

    [Fact]
    public void OnHashRejected_AllowsReRequest()
    {
        var tree = CreateTree(4);
        var picker = new HashPicker(
            new[] { tree.Root },
            filePieceOffsets: new[] { 0 },
            blocksPerPiece: 1,
            totalPieces: 4);

        var bitfield = new Bitfield(4);
        bitfield.SetPiece(0);

        var first = picker.PickHashRequest(bitfield);
        first.Should().NotBeNull();

        picker.OnHashRejected(first!.Value);

        var retry = picker.PickHashRequest(bitfield);
        retry.Should().NotBeNull();
    }

    // --- Multi-file binary search tests ---

    [Fact]
    public void PickHashRequest_MultiFile_ReturnsCorrectRoot()
    {
        var root0 = CreateHash(0x01);
        var root1 = CreateHash(0x02);
        var root2 = CreateHash(0x03);

        var picker = new HashPicker(
            fileRoots: new[] { root0, root1, root2 },
            filePieceOffsets: new[] { 0, 4, 7 },
            blocksPerPiece: 8,
            totalPieces: 10);

        var bitfield = new Bitfield(10);
        bitfield.SetPiece(0);
        var req = picker.PickHashRequest(bitfield);

        req.Should().NotBeNull();
        req!.Value.PiecesRoot.Should().Be(root0);
    }

    [Fact]
    public void PickHashRequest_MultiFile_LastFile()
    {
        var root0 = CreateHash(0x01);
        var root1 = CreateHash(0x02);

        var picker = new HashPicker(
            fileRoots: new[] { root0, root1 },
            filePieceOffsets: new[] { 0, 5 },
            blocksPerPiece: 4,
            totalPieces: 10);

        var bitfield = new Bitfield(10);
        bitfield.SetPiece(7);
        var req = picker.PickHashRequest(bitfield);

        req.Should().NotBeNull();
        req!.Value.PiecesRoot.Should().Be(root1);
    }

    [Fact]
    public void PickHashRequest_SingleFile_StillWorks()
    {
        var root = CreateHash(0xAA);

        var picker = new HashPicker(
            fileRoots: new[] { root },
            filePieceOffsets: new[] { 0 },
            blocksPerPiece: 8,
            totalPieces: 5);

        var bitfield = new Bitfield(5);
        bitfield.SetPiece(3);
        var req = picker.PickHashRequest(bitfield);

        req.Should().NotBeNull();
        req!.Value.PiecesRoot.Should().Be(root);
    }

    [Fact]
    public void Constructor_MismatchedArrayLengths_Throws()
    {
        var act = () => new HashPicker(
            fileRoots: new[] { CreateHash(0x01) },
            filePieceOffsets: new[] { 0, 5 },
            blocksPerPiece: 4,
            totalPieces: 10);

        act.Should().Throw<ArgumentException>();
    }

    private static SHA256Hash CreateHash(byte fill)
    {
        var bytes = new byte[32];
        Array.Fill(bytes, fill);
        return new SHA256Hash(bytes);
    }
}
