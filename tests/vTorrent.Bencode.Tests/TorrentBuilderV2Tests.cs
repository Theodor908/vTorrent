using FluentAssertions;
using vTorrent.Bencode.Builders;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class TorrentBuilderV2Tests
{
    private TorrentBuilder MinimalV1Builder() =>
        new TorrentBuilder()
            .WithAnnounce("http://tracker.example.com/announce")
            .WithName("test")
            .WithPieceLength(262144)
            .WithPieces(new PieceHashes(new byte[20]))
            .WithSingleFile("test.bin", 100);

    [Fact]
    public void WithSource_SetsSourceOnBuiltTorrent()
    {
        var torrent = MinimalV1Builder()
            .WithSource("PTP")
            .Build();

        torrent.Info.Source.Should().Be("PTP");
    }

    [Fact]
    public void WithUrlSeeds_SetsUrlListOnBuiltTorrent()
    {
        var torrent = MinimalV1Builder()
            .WithUrlSeeds("http://seed1.example.com/files/", "http://seed2.example.com/files/")
            .Build();

        torrent.UrlList.Should().HaveCount(2);
        torrent.UrlList![0].Should().Be("http://seed1.example.com/files/");
    }

    [Fact]
    public void WithHttpSeeds_SetsHttpSeedsOnBuiltTorrent()
    {
        var torrent = MinimalV1Builder()
            .WithHttpSeeds("http://seed.example.com/seed.php")
            .Build();

        torrent.HttpSeeds.Should().HaveCount(1);
    }

    [Fact]
    public void WithTrackerTier_MultiTier_ProducesCorrectAnnounceList()
    {
        var torrent = new TorrentBuilder()
            .WithAnnounce("http://primary.example.com/announce")
            .WithTrackerTier("http://primary.example.com/announce")
            .WithTrackerTier("http://backup1.example.com/announce", "http://backup2.example.com/announce")
            .WithName("test")
            .WithPieceLength(262144)
            .WithPieces(new PieceHashes(new byte[20]))
            .WithSingleFile("test.bin", 100)
            .Build();

        torrent.AnnounceList.Should().HaveCount(2);
        torrent.AnnounceList![0].Should().HaveCount(1);
        torrent.AnnounceList![1].Should().HaveCount(2);
    }

    [Fact]
    public void Build_V2Only_WithFileTreeAndNoPieces_Succeeds()
    {
        var piecesRoot = new byte[32];
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
        var root = new SHA256Hash(piecesRoot);
        var layers = new Dictionary<SHA256Hash, byte[]> { { root, new byte[32] } };

        var torrent = new TorrentBuilder()
            .WithAnnounce("http://tracker.example.com/announce")
            .WithName("v2test")
            .WithPieceLength(16384)
            .WithMultipleFiles("v2test", new TorrentFile { Path = new[] { "data.bin" }, Length = 65536, PiecesRoot = new SHA256Hash(piecesRoot) })
            .WithFileTree(fileTree)
            .WithMetaVersion(2)
            .WithPieceLayers(layers)
            .Build();

        torrent.Info.FileTreeV2.Should().NotBeNull();
        torrent.Info.MetaVersion.Should().Be(2);
        torrent.Info.Pieces.Should().BeNull();
        torrent.PieceLayers.Should().NotBeNull();
    }

    [Fact]
    public void Build_Trackerless_WithNoAnnounce_Succeeds()
    {
        var torrent = new TorrentBuilder()
            .WithName("dht-only")
            .WithPieceLength(262144)
            .WithPieces(new PieceHashes(new byte[20]))
            .WithSingleFile("test.bin", 100)
            .Build();

        torrent.Announce.Should().BeNullOrEmpty();
    }
}
