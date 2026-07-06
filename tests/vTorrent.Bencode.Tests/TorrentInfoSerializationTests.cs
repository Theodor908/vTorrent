using FluentAssertions;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class TorrentInfoSerializationTests
{
    [Fact]
    public void ToBDictionary_V2Only_EmitsMetaVersionAndFileTree()
    {
        var piecesRoot = new byte[32]; piecesRoot[0] = 0xEE;
        var fileTreeDict = new BDictionary
        {
            ["data.bin"] = new BDictionary
            {
                [""] = new BDictionary
                {
                    ["length"] = new BNumber(65536),
                    ["pieces root"] = new BString(piecesRoot)
                }
            }
        };
        var fileTree = FileTreeParser.Parse(fileTreeDict);

        var info = new TorrentInfo
        {
            Name = "v2test",
            PieceLength = 16384,
            Pieces = null,
            MetaVersion = 2,
            FileTreeV2 = fileTree,
            Files = FileTreeParser.Flatten(fileTree),
        };

        var dict = info.ToBDictionary(System.Text.Encoding.UTF8);

        dict.ContainsKey("meta version").Should().BeTrue();
        dict.GetNumber("meta version").Should().Be(2);
        dict.ContainsKey("file tree").Should().BeTrue();
        dict.ContainsKey("pieces").Should().BeFalse(); // no v1 pieces
    }

    [Fact]
    public void ToBDictionary_Hybrid_EmitsBothPiecesAndFileTree()
    {
        var piecesRoot = new byte[32];
        var fileTreeDict = new BDictionary
        {
            ["hybrid.txt"] = new BDictionary
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
            Name = "hybrid",
            PieceLength = 16384,
            Pieces = new PieceHashes(new byte[20]),
            MetaVersion = 2,
            FileTreeV2 = fileTree,
            Files = new[] { new TorrentFile { Path = new[] { "hybrid.txt" }, Length = 1024 } },
        };

        var dict = info.ToBDictionary(System.Text.Encoding.UTF8);

        dict.ContainsKey("pieces").Should().BeTrue();
        dict.ContainsKey("meta version").Should().BeTrue();
        dict.ContainsKey("file tree").Should().BeTrue();
    }

    [Fact]
    public void ToBDictionary_V1Only_NoV2Fields()
    {
        var info = new TorrentInfo
        {
            Name = "v1test",
            PieceLength = 262144,
            Pieces = new PieceHashes(new byte[20]),
            Files = new[] { new TorrentFile { Path = new[] { "v1test" }, Length = 100 } },
        };

        var dict = info.ToBDictionary(System.Text.Encoding.UTF8);

        dict.ContainsKey("pieces").Should().BeTrue();
        dict.ContainsKey("meta version").Should().BeFalse();
        dict.ContainsKey("file tree").Should().BeFalse();
    }
}
