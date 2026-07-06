using FluentAssertions;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class TorrentValidatorTests
{
    private static FileTree MakeFileTree(long length = 1024)
    {
        var root = new byte[32]; root[0] = 1;
        return FileTreeParser.Parse(new BDictionary
        {
            ["file.bin"] = new BDictionary
            {
                [""] = new BDictionary
                {
                    ["length"] = new BNumber(length),
                    ["pieces root"] = new BString(root)
                }
            }
        });
    }

    [Fact]
    public void V2_PieceLengthNotPowerOfTwo_Throws()
    {
        var info = new TorrentInfo
        {
            Name = "bad", PieceLength = 30000, MetaVersion = 2,
            FileTreeV2 = MakeFileTree(), Files = FileTreeParser.Flatten(MakeFileTree()),
        };
        var torrent = new Torrent { Announce = "http://t.co/a", Info = info };

        var act = () => TorrentValidator.Validate(torrent);
        act.Should().Throw<Exception>().WithMessage("*power of 2*");
    }

    [Fact]
    public void V2_PieceLengthTooSmall_Throws()
    {
        var info = new TorrentInfo
        {
            Name = "bad", PieceLength = 8192, MetaVersion = 2,
            FileTreeV2 = MakeFileTree(), Files = FileTreeParser.Flatten(MakeFileTree()),
        };
        var torrent = new Torrent { Announce = "http://t.co/a", Info = info };

        var act = () => TorrentValidator.Validate(torrent);
        act.Should().Throw<Exception>().WithMessage("*16 KiB*");
    }

    [Fact]
    public void V2_ValidPieceLength_DoesNotThrow()
    {
        var ft = MakeFileTree();
        var info = new TorrentInfo
        {
            Name = "good", PieceLength = 16384, MetaVersion = 2,
            FileTreeV2 = ft, Files = FileTreeParser.Flatten(ft),
        };
        var torrent = new Torrent { Announce = "http://t.co/a", Info = info };

        var act = () => TorrentValidator.Validate(torrent);
        act.Should().NotThrow();
    }

    [Fact]
    public void V1_AnyPieceLength_DoesNotThrow()
    {
        var info = new TorrentInfo
        {
            Name = "v1", PieceLength = 131072,
            Pieces = new PieceHashes(new byte[20]),
            Files = new[] { new TorrentFile { Path = new[] { "v1" }, Length = 100 } },
        };
        var torrent = new Torrent { Announce = "http://t.co/a", Info = info };

        var act = () => TorrentValidator.Validate(torrent);
        act.Should().NotThrow();
    }
}
