using FluentAssertions;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using vTorrent.Core;
using Xunit;
using vTorrent.Core.Download;

namespace vTorrent.Tests.Unit.Core;

public class DownloadCoordinatorHashGateTests
{
    [Fact]
    public void HashGateRequired_V1Torrent_ReturnsFalse()
    {
        var info = new TorrentInfo
        {
            Name = "v1", PieceLength = 16384,
            Pieces = new PieceHashes(new byte[20]),
            Files = new[] { new TorrentFile { Path = new[] { "v1" }, Length = 16384 } },
        };

        DownloadCoordinatorV2Helpers.RequiresHashGate(info).Should().BeFalse();
    }

    [Fact]
    public void HashGateRequired_V2Torrent_ReturnsTrue()
    {
        var root = new byte[32]; root[0] = 1;
        var ft = FileTreeParser.Parse(new BDictionary
        {
            ["f.bin"] = new BDictionary
            {
                [""] = new BDictionary
                {
                    ["length"] = new BNumber(16384),
                    ["pieces root"] = new BString(root)
                }
            }
        });

        var info = new TorrentInfo
        {
            Name = "v2", PieceLength = 16384, MetaVersion = 2,
            FileTreeV2 = ft,
            Files = FileTreeParser.Flatten(ft),
        };

        DownloadCoordinatorV2Helpers.RequiresHashGate(info).Should().BeTrue();
    }

    [Fact]
    public void HashGateRequired_HybridTorrent_ReturnsTrue()
    {
        var root = new byte[32];
        var ft = FileTreeParser.Parse(new BDictionary
        {
            ["f.bin"] = new BDictionary
            {
                [""] = new BDictionary
                {
                    ["length"] = new BNumber(16384),
                    ["pieces root"] = new BString(root)
                }
            }
        });

        var info = new TorrentInfo
        {
            Name = "hybrid", PieceLength = 16384,
            Pieces = new PieceHashes(new byte[20]),
            MetaVersion = 2,
            FileTreeV2 = ft,
            Files = new[] { new TorrentFile { Path = new[] { "f.bin" }, Length = 16384 } },
        };

        DownloadCoordinatorV2Helpers.RequiresHashGate(info).Should().BeTrue();
    }
}
