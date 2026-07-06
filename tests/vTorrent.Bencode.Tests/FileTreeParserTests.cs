using FluentAssertions;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class FileTreeParserTests
{
    /// <summary>
    /// Single file: { "file.txt": { "": { "length": 1024, "pieces root": &lt;32 bytes&gt; } } }
    /// </summary>
    [Fact]
    public void Parse_SingleFile_ReturnsOneFileEntry()
    {
        var piecesRoot = new byte[32]; piecesRoot[0] = 0xAA;
        var fileTree = new BDictionary
        {
            ["file.txt"] = new BDictionary
            {
                [""] = new BDictionary
                {
                    ["length"] = new BNumber(1024),
                    ["pieces root"] = new BString(piecesRoot)
                }
            }
        };

        var result = FileTreeParser.Parse(fileTree);

        result.Root.Children.Should().HaveCount(1);
        result.Root.Children.Should().ContainKey("file.txt");
        var entry = result.Root.Children["file.txt"].Entry;
        entry.Should().NotBeNull();
        entry!.Length.Should().Be(1024);
        entry.PiecesRoot!.Value.Bytes[0].Should().Be(0xAA);
    }

    /// <summary>
    /// Nested: { "dir": { "subfile.txt": { "": { "length": 2048, "pieces root": ... } } } }
    /// </summary>
    [Fact]
    public void Parse_NestedDirectory_ReturnsCorrectStructure()
    {
        var piecesRoot = new byte[32]; piecesRoot[0] = 0xBB;
        var fileTree = new BDictionary
        {
            ["dir"] = new BDictionary
            {
                ["subfile.txt"] = new BDictionary
                {
                    [""] = new BDictionary
                    {
                        ["length"] = new BNumber(2048),
                        ["pieces root"] = new BString(piecesRoot)
                    }
                }
            }
        };

        var result = FileTreeParser.Parse(fileTree);

        var dir = result.Root.Children["dir"];
        dir.Children.Should().ContainKey("subfile.txt");
        dir.Children["subfile.txt"].Entry!.Length.Should().Be(2048);
    }

    /// <summary>
    /// Multiple files at same level.
    /// </summary>
    [Fact]
    public void Parse_MultipleFiles_ReturnsAll()
    {
        var root1 = new byte[32]; root1[0] = 1;
        var root2 = new byte[32]; root2[0] = 2;
        var fileTree = new BDictionary
        {
            ["a.txt"] = new BDictionary
            {
                [""] = new BDictionary { ["length"] = new BNumber(100), ["pieces root"] = new BString(root1) }
            },
            ["b.txt"] = new BDictionary
            {
                [""] = new BDictionary { ["length"] = new BNumber(200), ["pieces root"] = new BString(root2) }
            }
        };

        var result = FileTreeParser.Parse(fileTree);
        result.Root.Children.Should().HaveCount(2);
    }

    [Fact]
    public void Flatten_SingleFile_ReturnsOneTorrentFile()
    {
        var piecesRoot = new byte[32];
        var fileTree = new BDictionary
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

        var tree = FileTreeParser.Parse(fileTree);
        var files = FileTreeParser.Flatten(tree);

        files.Should().HaveCount(1);
        files[0].Path.Should().Equal("test.bin");
        files[0].Length.Should().Be(4096);
    }

    [Fact]
    public void Flatten_NestedFiles_ReturnsCorrectPaths()
    {
        var root = new byte[32];
        var fileTree = new BDictionary
        {
            ["photos"] = new BDictionary
            {
                ["vacation"] = new BDictionary
                {
                    ["img1.jpg"] = new BDictionary
                    {
                        [""] = new BDictionary { ["length"] = new BNumber(5000), ["pieces root"] = new BString(root) }
                    },
                    ["img2.jpg"] = new BDictionary
                    {
                        [""] = new BDictionary { ["length"] = new BNumber(6000), ["pieces root"] = new BString(root) }
                    }
                }
            }
        };

        var tree = FileTreeParser.Parse(fileTree);
        var files = FileTreeParser.Flatten(tree);

        files.Should().HaveCount(2);
        files[0].Path.Should().Equal("photos", "vacation", "img1.jpg");
        files[1].Path.Should().Equal("photos", "vacation", "img2.jpg");
    }

    [Fact]
    public void Parse_ZeroLengthFile_HasNoPiecesRoot()
    {
        // BEP 52: zero-length files have no pieces root
        var fileTree = new BDictionary
        {
            ["empty.txt"] = new BDictionary
            {
                [""] = new BDictionary { ["length"] = new BNumber(0) }
            }
        };

        var result = FileTreeParser.Parse(fileTree);
        var entry = result.Root.Children["empty.txt"].Entry;
        entry!.Length.Should().Be(0);
        entry.PiecesRoot.Should().BeNull();
    }

    [Fact]
    public void Flatten_PreservesPiecesRoot()
    {
        var root1 = CreateHash(1);
        var root2 = CreateHash(2);

        var entry1 = new FileTreeEntry(65536, root1);
        var entry2 = new FileTreeEntry(131072, root2);

        var fileNode1 = FileTreeNode.File("file1.txt", entry1);
        var fileNode2 = FileTreeNode.File("file2.txt", entry2);

        var children = new SortedDictionary<string, FileTreeNode>(StringComparer.Ordinal)
        {
            ["file1.txt"] = fileNode1,
            ["file2.txt"] = fileNode2
        };
        var dirNode = FileTreeNode.Directory("root", children);
        var tree = new FileTree(dirNode);

        var files = FileTreeParser.Flatten(tree);

        files.Should().HaveCount(2);
        files[0].PiecesRoot.Should().Be(root1);
        files[1].PiecesRoot.Should().Be(root2);
    }

    [Fact]
    public void Flatten_NullPiecesRoot_ForV1Files()
    {
        var entry = new FileTreeEntry(1024, null);
        var fileNode = FileTreeNode.File("test.txt", entry);
        var children = new SortedDictionary<string, FileTreeNode>(StringComparer.Ordinal)
        {
            ["test.txt"] = fileNode
        };
        var tree = new FileTree(FileTreeNode.Directory("", children));

        var files = FileTreeParser.Flatten(tree);

        files.Should().ContainSingle();
        files[0].PiecesRoot.Should().BeNull();
    }

    private static SHA256Hash CreateHash(byte fill)
    {
        var bytes = new byte[32];
        Array.Fill(bytes, fill);
        return new SHA256Hash(bytes);
    }
}
