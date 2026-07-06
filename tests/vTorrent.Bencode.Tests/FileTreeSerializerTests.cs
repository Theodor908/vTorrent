using FluentAssertions;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class FileTreeSerializerTests
{
    [Fact]
    public void RoundTrip_SingleFile()
    {
        var piecesRoot = new byte[32]; piecesRoot[0] = 0xAA;
        var original = new BDictionary
        {
            ["test.bin"] = new BDictionary
            {
                [""] = new BDictionary
                {
                    ["length"] = new BNumber(4096),
                    ["pieces root"] = new BString(piecesRoot)
                }
            }
        };

        var tree = FileTreeParser.Parse(original);
        var serialized = FileTreeSerializer.Serialize(tree);

        // Re-parse and verify
        var reparsed = FileTreeParser.Parse(serialized);
        var files = FileTreeParser.Flatten(reparsed);
        files.Should().HaveCount(1);
        files[0].Length.Should().Be(4096);
    }

    [Fact]
    public void RoundTrip_NestedDirectories()
    {
        var root = new byte[32];
        var original = new BDictionary
        {
            ["docs"] = new BDictionary
            {
                ["readme.txt"] = new BDictionary
                {
                    [""] = new BDictionary
                    {
                        ["length"] = new BNumber(100),
                        ["pieces root"] = new BString(root)
                    }
                }
            }
        };

        var tree = FileTreeParser.Parse(original);
        var serialized = FileTreeSerializer.Serialize(tree);
        var reparsed = FileTreeParser.Parse(serialized);
        var files = FileTreeParser.Flatten(reparsed);

        files.Should().HaveCount(1);
        files[0].Path.Should().Equal("docs", "readme.txt");
    }

    [Fact]
    public void Serialize_ZeroLengthFile_NoPiecesRoot()
    {
        var original = new BDictionary
        {
            ["empty.txt"] = new BDictionary
            {
                [""] = new BDictionary { ["length"] = new BNumber(0) }
            }
        };

        var tree = FileTreeParser.Parse(original);
        var serialized = FileTreeSerializer.Serialize(tree);
        var reparsed = FileTreeParser.Parse(serialized);

        reparsed.Root.Children["empty.txt"].Entry!.Length.Should().Be(0);
        reparsed.Root.Children["empty.txt"].Entry!.PiecesRoot.Should().BeNull();
    }
}
