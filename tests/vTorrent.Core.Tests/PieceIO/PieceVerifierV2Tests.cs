using FluentAssertions;
using System.Security.Cryptography;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Merkle;
using vTorrent.Core.PieceIO;
using Xunit;

namespace vTorrent.Tests.Unit.PieceIO;

public class PieceVerifierV2Tests
{
    private const int BlockSize = 16384;

    [Fact]
    public void VerifyPiece_V1_UsesLegacySha1Path()
    {
        var data = new byte[BlockSize];
        data[0] = 42;
        var expectedHash = SHA1.HashData(data);
        var pieces = new PieceHashes(expectedHash);

        var info = new TorrentInfo
        {
            Name = "v1test", PieceLength = BlockSize,
            Pieces = pieces,
            Files = new[] { new TorrentFile { Path = new[] { "v1test" }, Length = BlockSize } },
        };

        var verifier = new PieceVerifier(info);

        verifier.VerifyPiece(0, data).Should().BeTrue();
    }

    [Fact]
    public void VerifyPiece_V1_WrongData_ReturnsFalse()
    {
        var data = new byte[BlockSize];
        var wrongHash = new byte[20];
        var pieces = new PieceHashes(wrongHash);

        var info = new TorrentInfo
        {
            Name = "v1bad", PieceLength = BlockSize,
            Pieces = pieces,
            Files = new[] { new TorrentFile { Path = new[] { "v1bad" }, Length = BlockSize } },
        };

        var verifier = new PieceVerifier(info);
        verifier.VerifyPiece(0, data).Should().BeFalse();
    }

    [Fact]
    public void VerifyBlock_V2_CorrectHash_ReturnsTrue()
    {
        var blockData = new byte[BlockSize];
        blockData[0] = 99;
        var blockHash = new SHA256Hash(SHA256.HashData(blockData));

        var tree = MerkleTree.FromLeaves(new[] { blockHash });

        var info = new TorrentInfo
        {
            Name = "v2test", PieceLength = BlockSize, MetaVersion = 2,
            FileTreeV2 = CreateSimpleFileTree(BlockSize),
            Files = new[] { new TorrentFile { Path = new[] { "v2test" }, Length = BlockSize } },
        };

        var verifier = new PieceVerifier(info, merkleTrees: new[] { tree });

        verifier.VerifyBlock(fileIndex: 0, blockIndex: 0, blockData).Should().BeTrue();
    }

    [Fact]
    public void VerifyBlock_V2_WrongData_ReturnsFalse()
    {
        var correctData = new byte[BlockSize];
        correctData[0] = 99;
        var correctHash = new SHA256Hash(SHA256.HashData(correctData));
        var tree = MerkleTree.FromLeaves(new[] { correctHash });

        var info = new TorrentInfo
        {
            Name = "v2bad", PieceLength = BlockSize, MetaVersion = 2,
            FileTreeV2 = CreateSimpleFileTree(BlockSize),
            Files = new[] { new TorrentFile { Path = new[] { "v2bad" }, Length = BlockSize } },
        };

        var verifier = new PieceVerifier(info, merkleTrees: new[] { tree });

        var wrongData = new byte[BlockSize];
        verifier.VerifyBlock(fileIndex: 0, blockIndex: 0, wrongData).Should().BeFalse();
    }

    [Fact]
    public void VerifyPiece_Hybrid_ChecksBothSha1AndMerkle()
    {
        var data = new byte[BlockSize];
        data[0] = 77;

        var sha1Hash = SHA1.HashData(data);
        var pieces = new PieceHashes(sha1Hash);

        var blockHash = new SHA256Hash(SHA256.HashData(data));
        var tree = MerkleTree.FromLeaves(new[] { blockHash });

        var info = new TorrentInfo
        {
            Name = "hybrid", PieceLength = BlockSize,
            Pieces = pieces, MetaVersion = 2,
            FileTreeV2 = CreateSimpleFileTree(BlockSize),
            Files = new[] { new TorrentFile { Path = new[] { "hybrid" }, Length = BlockSize } },
        };

        var verifier = new PieceVerifier(info, merkleTrees: new[] { tree });

        verifier.VerifyPiece(0, data).Should().BeTrue();
    }

    private static FileTree CreateSimpleFileTree(long length)
    {
        var root = new byte[32];
        var dict = new vTorrent.Bencode.Objects.BDictionary
        {
            ["file.bin"] = new vTorrent.Bencode.Objects.BDictionary
            {
                [""] = new vTorrent.Bencode.Objects.BDictionary
                {
                    ["length"] = new vTorrent.Bencode.Objects.BNumber(length),
                    ["pieces root"] = new vTorrent.Bencode.Objects.BString(root)
                }
            }
        };
        return FileTreeParser.Parse(dict);
    }
}
