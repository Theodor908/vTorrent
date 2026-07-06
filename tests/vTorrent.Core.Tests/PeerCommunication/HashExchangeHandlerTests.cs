using FluentAssertions;
using Moq;
using System.Security.Cryptography;
using vTorrent.Bencode.Torrents;
using vTorrent.Core;
using vTorrent.Core.Merkle;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Utilities;
using Xunit;
using vTorrent.Core.Download;

namespace vTorrent.Tests.Unit.PeerCommunication;

public class HashExchangeHandlerTests
{
    private static SHA256Hash HashBlock(byte value)
        => new(SHA256.HashData(new[] { value }));

    private static SHA256Hash CreateHash(byte fill)
    {
        var bytes = new byte[32];
        Array.Fill(bytes, fill);
        return new SHA256Hash(bytes);
    }

    [Fact]
    public async Task OnHashRequest_HasTree_SendsHashes()
    {
        var leaves = new SHA256Hash[4];
        for (int i = 0; i < 4; i++)
            leaves[i] = HashBlock((byte)i);
        var tree = MerkleTree.FromLeaves(leaves);

        var trees = new Dictionary<SHA256Hash, MerkleTree>
        {
            { tree.Root, tree }
        };

        var handler = new HashExchangeHandler(trees);
        var peer = new Mock<IPeerConnection>();
        HashesMessage? sentMessage = null;
        peer.Setup(p => p.SendHashesAsync(It.IsAny<HashesMessage>(), It.IsAny<CancellationToken>()))
            .Callback<HashesMessage, CancellationToken>((msg, _) => sentMessage = msg)
            .Returns(Task.CompletedTask);

        var request = new HashRequestMessage
        {
            PiecesRoot = tree.Root,
            BaseLayer = 0,
            Index = 0,
            Length = 2,
            ProofLayers = 1
        };

        await handler.OnHashRequestAsync(peer.Object, request, CancellationToken.None);

        sentMessage.Should().NotBeNull();
        sentMessage!.Value.PiecesRoot.Should().Be(tree.Root);
        sentMessage.Value.Hashes.Should().HaveCount(3); // 2 data + 1 proof
    }

    [Fact]
    public async Task OnHashRequest_UnknownRoot_SendsReject()
    {
        var handler = new HashExchangeHandler(new Dictionary<SHA256Hash, MerkleTree>());
        var peer = new Mock<IPeerConnection>();
        HashRejectMessage? sentReject = null;
        peer.Setup(p => p.SendHashRejectAsync(It.IsAny<HashRejectMessage>(), It.IsAny<CancellationToken>()))
            .Callback<HashRejectMessage, CancellationToken>((msg, _) => sentReject = msg)
            .Returns(Task.CompletedTask);

        var unknownRoot = HashBlock(0xFF);
        var request = new HashRequestMessage
        {
            PiecesRoot = unknownRoot,
            BaseLayer = 0, Index = 0, Length = 2, ProofLayers = 1
        };

        await handler.OnHashRequestAsync(peer.Object, request, CancellationToken.None);

        sentReject.Should().NotBeNull();
        sentReject!.Value.PiecesRoot.Should().Be(unknownRoot);
    }

    [Fact]
    public async Task OnHashesReceived_ValidProof_DoesNotThrow()
    {
        var leaves = new SHA256Hash[4];
        for (int i = 0; i < 4; i++)
            leaves[i] = HashBlock((byte)i);
        var fullTree = MerkleTree.FromLeaves(leaves);

        var trees = new Dictionary<SHA256Hash, MerkleTree>
        {
            { fullTree.Root, fullTree }
        };

        var handler = new HashExchangeHandler(trees);
        var peer = new Mock<IPeerConnection>();

        var msg = new HashesMessage
        {
            PiecesRoot = fullTree.Root,
            BaseLayer = 0, Index = 0, Length = 2, ProofLayers = 1,
            Hashes = new[] { leaves[0], leaves[1], HashBlock(0xCC) }
        };

        var act = () => handler.OnHashesReceivedAsync(peer.Object, msg, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OnHashRejectAsync_NotifiesHashPicker()
    {
        var root = CreateHash(0x01);
        var tree = MerkleTree.FromLeaves(new[] { CreateHash(0xAA), CreateHash(0xBB) });
        var trees = new Dictionary<SHA256Hash, MerkleTree> { [root] = tree };

        var picker = new HashPicker(
            new[] { root }, new[] { 0 }, blocksPerPiece: 1, totalPieces: 2);

        var handler = new HashExchangeHandler(trees, picker);
        var peer = new Mock<IPeerConnection>().Object;

        var msg = new HashRejectMessage
        {
            PiecesRoot = root, BaseLayer = 0, Index = 0, Length = 2, ProofLayers = 0
        };

        var bitfield = new Bitfield(2);
        bitfield.SetPiece(0);
        picker.PickHashRequest(bitfield);

        await handler.OnHashRejectAsync(peer, msg, CancellationToken.None);

        var req = picker.PickHashRequest(bitfield);
        req.Should().NotBeNull();
    }

    [Fact]
    public async Task OnHashesReceivedAsync_UnknownRoot_Ignored()
    {
        var handler = new HashExchangeHandler(
            new Dictionary<SHA256Hash, MerkleTree>(), null);
        var peer = new Mock<IPeerConnection>().Object;

        var msg = new HashesMessage
        {
            PiecesRoot = CreateHash(0xFF), BaseLayer = 0, Index = 0,
            Length = 1, ProofLayers = 0, Hashes = new[] { CreateHash(0x01) }
        };

        await handler.OnHashesReceivedAsync(peer, msg, CancellationToken.None);
    }

    [Fact]
    public async Task OnHashesReceivedAsync_MalformedMessage_Ignored()
    {
        var root = CreateHash(0x01);
        var tree = MerkleTree.FromLeaves(new[] { CreateHash(0xAA), CreateHash(0xBB) });
        var trees = new Dictionary<SHA256Hash, MerkleTree> { [root] = tree };

        var handler = new HashExchangeHandler(trees, null);
        var peer = new Mock<IPeerConnection>().Object;

        var msg = new HashesMessage
        {
            PiecesRoot = root, BaseLayer = 0, Index = 0,
            Length = 4, ProofLayers = 0, Hashes = new[] { CreateHash(0x01) }
        };

        await handler.OnHashesReceivedAsync(peer, msg, CancellationToken.None);
    }
}
