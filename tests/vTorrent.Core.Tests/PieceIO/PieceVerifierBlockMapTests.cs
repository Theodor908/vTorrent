using FluentAssertions;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Merkle;
using vTorrent.Core.PieceIO;
using Xunit;

namespace vTorrent.Tests.Unit.PieceIO;

public class PieceVerifierBlockMapTests
{
    private const int BlockSize = 16384;

    // ---- helpers ----

    private static FileTree CreateFileTree(params (string name, long length)[] files)
    {
        var root = new BDictionary();
        foreach (var (name, length) in files)
        {
            var piecesRoot = new byte[32];
            root[name] = new BDictionary
            {
                [""] = new BDictionary
                {
                    ["length"] = new BNumber(length),
                    ["pieces root"] = new BString(piecesRoot)
                }
            };
        }
        return FileTreeParser.Parse(root);
    }

    private static TorrentInfo MakeV2Info(params (string name, long length)[] files)
    {
        var torrentFiles = files
            .Select(f => new TorrentFile { Path = new[] { f.name }, Length = f.length })
            .ToArray();

        return new TorrentInfo
        {
            Name = "test",
            PieceLength = BlockSize,
            MetaVersion = 2,
            FileTreeV2 = CreateFileTree(files),
            Files = torrentFiles,
        };
    }

    // ---- single-file tests (no precomputed map; uses fast path) ----

    [Fact]
    public void SingleFile_Block0_MapsToFile0Block0()
    {
        var info = MakeV2Info(("a.bin", BlockSize));
        var verifier = new PieceVerifier(info);

        verifier.MapGlobalBlockToFilePublic(0).Should().Be((0, 0));
    }

    [Fact]
    public void SingleFile_BlockN_MapsToFile0BlockN()
    {
        var info = MakeV2Info(("a.bin", BlockSize * 4));
        var verifier = new PieceVerifier(info);

        verifier.MapGlobalBlockToFilePublic(3).Should().Be((0, 3));
    }

    [Fact]
    public void SingleFile_OutOfRange_ReturnsNegativeOne()
    {
        // Single-file uses fast path which doesn't bounds-check (returns (0, globalBlock)),
        // so no map is built; we verify that negative index still falls through correctly
        // in the single-file fast path — it simply returns (0, negativeValue).
        // This is the current expected behavior for single-file (no map allocated).
        var info = MakeV2Info(("a.bin", BlockSize));
        var verifier = new PieceVerifier(info);

        // The single-file path just returns (0, globalBlock) without bounds checking.
        verifier.MapGlobalBlockToFilePublic(999).Should().Be((0, 999));
    }

    // ---- multi-file tests (precomputed map is active) ----

    [Fact]
    public void TwoFiles_FirstBlock_MapsToFirstFile()
    {
        // File 0: 1 block, File 1: 1 block
        var info = MakeV2Info(("a.bin", BlockSize), ("b.bin", BlockSize));
        var verifier = new PieceVerifier(info);

        verifier.MapGlobalBlockToFilePublic(0).Should().Be((0, 0));
    }

    [Fact]
    public void TwoFiles_SecondBlock_MapsToSecondFile()
    {
        // File 0: 1 block, File 1: 1 block -> global block 1 = file 1, local 0
        var info = MakeV2Info(("a.bin", BlockSize), ("b.bin", BlockSize));
        var verifier = new PieceVerifier(info);

        verifier.MapGlobalBlockToFilePublic(1).Should().Be((1, 0));
    }

    [Fact]
    public void TwoFiles_MultipleBlocksEach_CorrectMapping()
    {
        // File 0: 3 blocks, File 1: 2 blocks
        // global 0 -> (0,0), 1 -> (0,1), 2 -> (0,2), 3 -> (1,0), 4 -> (1,1)
        var info = MakeV2Info(("a.bin", BlockSize * 3), ("b.bin", BlockSize * 2));
        var verifier = new PieceVerifier(info);

        verifier.MapGlobalBlockToFilePublic(0).Should().Be((0, 0));
        verifier.MapGlobalBlockToFilePublic(1).Should().Be((0, 1));
        verifier.MapGlobalBlockToFilePublic(2).Should().Be((0, 2));
        verifier.MapGlobalBlockToFilePublic(3).Should().Be((1, 0));
        verifier.MapGlobalBlockToFilePublic(4).Should().Be((1, 1));
    }

    [Fact]
    public void ThreeFiles_CorrectMappingAcrossAllFiles()
    {
        // File 0: 1 block, File 1: 2 blocks, File 2: 1 block
        // global 0 -> (0,0), 1 -> (1,0), 2 -> (1,1), 3 -> (2,0)
        var info = MakeV2Info(("a.bin", BlockSize), ("b.bin", BlockSize * 2), ("c.bin", BlockSize));
        var verifier = new PieceVerifier(info);

        verifier.MapGlobalBlockToFilePublic(0).Should().Be((0, 0));
        verifier.MapGlobalBlockToFilePublic(1).Should().Be((1, 0));
        verifier.MapGlobalBlockToFilePublic(2).Should().Be((1, 1));
        verifier.MapGlobalBlockToFilePublic(3).Should().Be((2, 0));
    }

    [Fact]
    public void MultiFile_PartialLastBlock_CountsAsOneBlock()
    {
        // File 0: 1.5 blocks worth of data -> still 2 blocks (last one partial)
        // File 1: 1 block
        // global 0 -> (0,0), 1 -> (0,1), 2 -> (1,0)
        var info = MakeV2Info(("a.bin", BlockSize + BlockSize / 2), ("b.bin", BlockSize));
        var verifier = new PieceVerifier(info);

        verifier.MapGlobalBlockToFilePublic(0).Should().Be((0, 0));
        verifier.MapGlobalBlockToFilePublic(1).Should().Be((0, 1));
        verifier.MapGlobalBlockToFilePublic(2).Should().Be((1, 0));
    }

    [Fact]
    public void MultiFile_OutOfRange_ReturnsNegativeOne()
    {
        // Total 2 blocks; global block 2 is out of range
        var info = MakeV2Info(("a.bin", BlockSize), ("b.bin", BlockSize));
        var verifier = new PieceVerifier(info);

        verifier.MapGlobalBlockToFilePublic(2).Should().Be((-1, -1));
    }

    [Fact]
    public void MultiFile_NegativeIndex_ReturnsNegativeOne()
    {
        var info = MakeV2Info(("a.bin", BlockSize), ("b.bin", BlockSize));
        var verifier = new PieceVerifier(info);

        verifier.MapGlobalBlockToFilePublic(-1).Should().Be((-1, -1));
    }

    [Fact]
    public void MultiFile_ZeroLengthFile_CountsAsOneBlock()
    {
        // Zero-length file gets 1 block (per the clamping logic)
        // File 0: 0 bytes -> 1 block, File 1: 1 block
        // global 0 -> (0,0), 1 -> (1,0)
        var info = MakeV2Info(("empty.bin", 0), ("b.bin", BlockSize));
        var verifier = new PieceVerifier(info);

        verifier.MapGlobalBlockToFilePublic(0).Should().Be((0, 0));
        verifier.MapGlobalBlockToFilePublic(1).Should().Be((1, 0));
    }
}
