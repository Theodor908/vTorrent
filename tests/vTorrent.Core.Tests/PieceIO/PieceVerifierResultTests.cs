using FluentAssertions;
using System.Security.Cryptography;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Merkle;
using vTorrent.Core.PieceIO;
using Xunit;

namespace vTorrent.Tests.Unit.PieceIO;

public class PieceVerifierResultTests
{
    private const int BlockSize = 16384;

    // -------------------------------------------------------------------------
    // V1 tests
    // -------------------------------------------------------------------------

    [Fact]
    public void VerifyPieceResult_V1_CorrectHash_ReturnsValid()
    {
        var data = new byte[BlockSize];
        data[0] = 42;
        var expectedHash = SHA1.HashData(data);
        var pieces = new PieceHashes(expectedHash);

        var info = new TorrentInfo
        {
            Name = "v1ok", PieceLength = BlockSize,
            Pieces = pieces,
            Files = new[] { new TorrentFile { Path = new[] { "v1ok" }, Length = BlockSize } },
        };

        var verifier = new PieceVerifier(info);

        verifier.VerifyPieceResult(0, data).Should().Be(PieceVerifyResult.Valid);
    }

    [Fact]
    public void VerifyPieceResult_V1_WrongHash_ReturnsCorruptV1()
    {
        var data = new byte[BlockSize];
        var wrongHash = new byte[20]; // all zeros — wrong hash
        var pieces = new PieceHashes(wrongHash);

        var info = new TorrentInfo
        {
            Name = "v1bad", PieceLength = BlockSize,
            Pieces = pieces,
            Files = new[] { new TorrentFile { Path = new[] { "v1bad" }, Length = BlockSize } },
        };

        var verifier = new PieceVerifier(info);

        verifier.VerifyPieceResult(0, data).Should().Be(PieceVerifyResult.CorruptV1);
    }

    // -------------------------------------------------------------------------
    // Null / empty data
    // -------------------------------------------------------------------------

    [Fact]
    public void VerifyPieceResult_NullData_ReturnsCorruptV1()
    {
        var data = new byte[BlockSize];
        var hash = SHA1.HashData(data);
        var pieces = new PieceHashes(hash);

        var info = new TorrentInfo
        {
            Name = "nulltest", PieceLength = BlockSize,
            Pieces = pieces,
            Files = new[] { new TorrentFile { Path = new[] { "nulltest" }, Length = BlockSize } },
        };

        var verifier = new PieceVerifier(info);

        verifier.VerifyPieceResult(0, null!).Should().Be(PieceVerifyResult.CorruptV1);
    }

    // -------------------------------------------------------------------------
    // Hybrid: V1 pass + V2 fail → Inconsistent
    // -------------------------------------------------------------------------

    [Fact]
    public void VerifyPieceResult_Hybrid_V1PassV2Fail_ReturnsInconsistent()
    {
        var data = new byte[BlockSize];
        data[0] = 77;

        // Correct SHA-1 hash so V1 passes
        var sha1Hash = SHA1.HashData(data);
        var pieces = new PieceHashes(sha1Hash);

        // Wrong merkle tree (built from different data) so V2 fails
        var differentData = new byte[BlockSize];
        differentData[0] = 99; // different content
        var wrongBlockHash = new SHA256Hash(SHA256.HashData(differentData));
        var wrongTree = MerkleTree.FromLeaves(new[] { wrongBlockHash });

        var info = new TorrentInfo
        {
            Name = "hybrid-inconsistent", PieceLength = BlockSize,
            Pieces = pieces, MetaVersion = 2,
            FileTreeV2 = CreateSimpleFileTree(BlockSize),
            Files = new[] { new TorrentFile { Path = new[] { "hybrid-inconsistent" }, Length = BlockSize } },
        };

        var verifier = new PieceVerifier(info, merkleTrees: new[] { wrongTree });

        verifier.VerifyPieceResult(0, data).Should().Be(PieceVerifyResult.Inconsistent);
    }

    // -------------------------------------------------------------------------
    // Legacy bool VerifyPiece wrapper
    // -------------------------------------------------------------------------

    [Fact]
    public void VerifyPiece_LegacyBool_ValidData_ReturnsTrue()
    {
        var data = new byte[BlockSize];
        data[0] = 55;
        var expectedHash = SHA1.HashData(data);
        var pieces = new PieceHashes(expectedHash);

        var info = new TorrentInfo
        {
            Name = "legacy-ok", PieceLength = BlockSize,
            Pieces = pieces,
            Files = new[] { new TorrentFile { Path = new[] { "legacy-ok" }, Length = BlockSize } },
        };

        var verifier = new PieceVerifier(info);

        verifier.VerifyPiece(0, data).Should().BeTrue();
    }

    [Fact]
    public void VerifyPiece_LegacyBool_InvalidData_ReturnsFalse()
    {
        var data = new byte[BlockSize];
        var wrongHash = new byte[20];
        var pieces = new PieceHashes(wrongHash);

        var info = new TorrentInfo
        {
            Name = "legacy-bad", PieceLength = BlockSize,
            Pieces = pieces,
            Files = new[] { new TorrentFile { Path = new[] { "legacy-bad" }, Length = BlockSize } },
        };

        var verifier = new PieceVerifier(info);

        verifier.VerifyPiece(0, data).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

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
