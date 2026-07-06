using FluentAssertions;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class TorrentInfoV2Tests
{
    [Fact]
    public void V1Torrent_HasVersionV1()
    {
        var info = new TorrentInfo
        {
            Name = "test",
            PieceLength = 262144,
            Pieces = new PieceHashes(new byte[20]),
            Files = new[] { new TorrentFile { Path = new[] { "test" }, Length = 100 } },
        };

        info.Version.Should().Be(TorrentVersion.V1);
        info.MetaVersion.Should().BeNull();
        info.FileTreeV2.Should().BeNull();
    }

    [Fact]
    public void V2Torrent_HasVersionV2()
    {
        var piecesRoot = new byte[32]; piecesRoot[0] = 0xAA;
        var fileTreeDict = new BDictionary
        {
            ["test.bin"] = new BDictionary
            {
                [""] = new BDictionary
                {
                    ["length"] = new BNumber(1024),
                    ["pieces root"] = new BString(piecesRoot)
                }
            }
        };
        var fileTree = FileTreeParser.Parse(fileTreeDict);

        var info = new TorrentInfo
        {
            Name = "test",
            PieceLength = 16384,
            Pieces = null,
            MetaVersion = 2,
            FileTreeV2 = fileTree,
            Files = FileTreeParser.Flatten(fileTree),
        };

        info.Version.Should().Be(TorrentVersion.V2);
    }

    [Fact]
    public void HybridTorrent_HasVersionHybrid()
    {
        var piecesRoot = new byte[32];
        var fileTreeDict = new BDictionary
        {
            ["test.bin"] = new BDictionary
            {
                [""] = new BDictionary
                {
                    ["length"] = new BNumber(1024),
                    ["pieces root"] = new BString(piecesRoot)
                }
            }
        };
        var fileTree = FileTreeParser.Parse(fileTreeDict);

        var info = new TorrentInfo
        {
            Name = "test",
            PieceLength = 16384,
            Pieces = new PieceHashes(new byte[20]),
            MetaVersion = 2,
            FileTreeV2 = fileTree,
            Files = new[] { new TorrentFile { Path = new[] { "test.bin" }, Length = 1024 } },
        };

        info.Version.Should().Be(TorrentVersion.Hybrid);
    }

    [Fact]
    public void ParseV2InfoDict_DetectsMetaVersionAndFileTree()
    {
        var piecesRoot = new byte[32]; piecesRoot[0] = 0xCC;
        var infoDict = new BDictionary
        {
            ["name"] = new BString("v2test"),
            ["piece length"] = new BNumber(16384),
            ["meta version"] = new BNumber(2),
            ["file tree"] = new BDictionary
            {
                ["data.bin"] = new BDictionary
                {
                    [""] = new BDictionary
                    {
                        ["length"] = new BNumber(32768),
                        ["pieces root"] = new BString(piecesRoot)
                    }
                }
            }
        };

        var info = TorrentInfo.FromBDictionary(infoDict, System.Text.Encoding.UTF8);

        info.MetaVersion.Should().Be(2);
        info.FileTreeV2.Should().NotBeNull();
        info.Version.Should().Be(TorrentVersion.V2);
        info.Files.Should().HaveCount(1);
        info.Files[0].Length.Should().Be(32768);
        info.Files[0].Path.Should().Equal("data.bin");
    }

    [Fact]
    public void ParseHybridInfoDict_HasBothPiecesAndFileTree()
    {
        var piecesRoot = new byte[32];
        var infoDict = new BDictionary
        {
            ["name"] = new BString("hybrid"),
            ["piece length"] = new BNumber(16384),
            ["pieces"] = new BString(new byte[20]),
            ["meta version"] = new BNumber(2),
            ["length"] = new BNumber(1024),
            ["file tree"] = new BDictionary
            {
                ["hybrid.txt"] = new BDictionary
                {
                    [""] = new BDictionary
                    {
                        ["length"] = new BNumber(1024),
                        ["pieces root"] = new BString(piecesRoot)
                    }
                }
            }
        };

        var info = TorrentInfo.FromBDictionary(infoDict, System.Text.Encoding.UTF8);

        info.Version.Should().Be(TorrentVersion.Hybrid);
        info.Pieces.Should().NotBeNull();
        info.FileTreeV2.Should().NotBeNull();
    }
}
