using FluentAssertions;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class TorrentV2RoundTripTests
{
    [Fact]
    public void ToBDictionary_WithPieceLayers_IncludesKey()
    {
        var root = new SHA256Hash(new byte[32]);
        var layerData = new byte[64]; // 2 piece hashes

        var piecesRoot = new byte[32];
        var fileTree = FileTreeParser.Parse(new BDictionary
        {
            ["file.bin"] = new BDictionary
            {
                [""] = new BDictionary
                {
                    ["length"] = new BNumber(32768),
                    ["pieces root"] = new BString(piecesRoot)
                }
            }
        });

        var torrent = new Torrent
        {
            Announce = "http://tracker.example.com/announce",
            Info = new TorrentInfo
            {
                Name = "test",
                PieceLength = 16384,
                MetaVersion = 2,
                FileTreeV2 = fileTree,
                Files = FileTreeParser.Flatten(fileTree),
            },
            PieceLayers = new Dictionary<SHA256Hash, byte[]> { { root, layerData } },
        };

        var dict = torrent.ToBDictionary();

        dict.ContainsKey("piece layers").Should().BeTrue();
    }

    [Fact]
    public void GetInfoHash_V2Torrent_HasV2Hash()
    {
        var piecesRoot = new byte[32];
        var fileTree = FileTreeParser.Parse(new BDictionary
        {
            ["file.bin"] = new BDictionary
            {
                [""] = new BDictionary
                {
                    ["length"] = new BNumber(1024),
                    ["pieces root"] = new BString(piecesRoot)
                }
            }
        });

        var torrent = new Torrent
        {
            Announce = "http://tracker.example.com/announce",
            Info = new TorrentInfo
            {
                Name = "v2test",
                PieceLength = 16384,
                MetaVersion = 2,
                FileTreeV2 = fileTree,
                Files = FileTreeParser.Flatten(fileTree),
            },
        };

        var infoHash = torrent.GetInfoHash();

        infoHash.HasV2.Should().BeTrue();
        infoHash.HasV1.Should().BeFalse();
        infoHash.Version.Should().Be(TorrentVersion.V2);
        infoHash.PrimaryHex.Should().HaveLength(40);
    }

    [Fact]
    public void GetInfoHash_HybridTorrent_HasBothHashes()
    {
        var piecesRoot = new byte[32];
        var fileTree = FileTreeParser.Parse(new BDictionary
        {
            ["file.bin"] = new BDictionary
            {
                [""] = new BDictionary
                {
                    ["length"] = new BNumber(1024),
                    ["pieces root"] = new BString(piecesRoot)
                }
            }
        });

        var torrent = new Torrent
        {
            Announce = "http://tracker.example.com/announce",
            Info = new TorrentInfo
            {
                Name = "hybrid",
                PieceLength = 16384,
                Pieces = new PieceHashes(new byte[20]),
                MetaVersion = 2,
                FileTreeV2 = fileTree,
                Files = new[] { new TorrentFile { Path = new[] { "file.bin" }, Length = 1024 } },
            },
        };

        var infoHash = torrent.GetInfoHash();

        infoHash.HasV1.Should().BeTrue();
        infoHash.HasV2.Should().BeTrue();
        infoHash.IsHybrid.Should().BeTrue();
    }
}
