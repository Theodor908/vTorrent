using FluentAssertions;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class TorrentSourceTagTests
{
    private static TorrentInfo MakeV1Info(string name = "test", string? source = null) =>
        new TorrentInfo
        {
            Name = name,
            PieceLength = 262144,
            Pieces = new PieceHashes(new byte[20]),
            Files = new[] { new TorrentFile { Path = new[] { name }, Length = 1234 } },
            Source = source,
        };

    [Fact]
    public void ToBDictionary_WithSource_EmitsSourceKey()
    {
        var info = MakeV1Info(source: "PTP");

        var dict = info.ToBDictionary(System.Text.Encoding.UTF8);

        dict.ContainsKey("source").Should().BeTrue();
        dict.GetString("source").Should().Be("PTP");
    }

    [Fact]
    public void ToBDictionary_WithoutSource_OmitsSourceKey()
    {
        var info = MakeV1Info(source: null);

        var dict = info.ToBDictionary(System.Text.Encoding.UTF8);

        dict.ContainsKey("source").Should().BeFalse();
    }

    [Fact]
    public void FromBDictionary_WithSource_ParsesSourceField()
    {
        var enc = System.Text.Encoding.UTF8;
        var dict = new BDictionary
        {
            ["name"] = new BString("album", enc),
            ["piece length"] = new BNumber(262144),
            ["pieces"] = new BString(new byte[20]),
            ["length"] = new BNumber(5000),
            ["source"] = new BString("RED", enc),
        };

        var info = TorrentInfo.FromBDictionary(dict, enc);

        info.Source.Should().Be("RED");
    }

    [Fact]
    public void FromBDictionary_WithoutSource_SourceIsNull()
    {
        var enc = System.Text.Encoding.UTF8;
        var dict = new BDictionary
        {
            ["name"] = new BString("album", enc),
            ["piece length"] = new BNumber(262144),
            ["pieces"] = new BString(new byte[20]),
            ["length"] = new BNumber(5000),
        };

        var info = TorrentInfo.FromBDictionary(dict, enc);

        info.Source.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_SourceTag_PreservedThroughSerializeAndParse()
    {
        var enc = System.Text.Encoding.UTF8;
        var original = MakeV1Info(source: "BTN");

        var dict = original.ToBDictionary(enc);
        var parsed = TorrentInfo.FromBDictionary(dict, enc);

        parsed.Source.Should().Be("BTN");
    }

    [Fact]
    public void Source_ChangesInfoHash()
    {
        var enc = System.Text.Encoding.UTF8;
        var withSource = MakeV1Info(source: "PTP");
        var withoutSource = MakeV1Info(source: null);

        var bytesWith = withSource.ToBDictionary(enc).EncodeAsBytes();
        var bytesWithout = withoutSource.ToBDictionary(enc).EncodeAsBytes();

        bytesWith.Should().NotEqual(bytesWithout);
    }

    [Fact]
    public void FullTorrent_RoundTrip_PreservesSource()
    {
        var enc = System.Text.Encoding.UTF8;

        // Build a full torrent BDictionary with source inside the info dict
        var infoDict = new BDictionary
        {
            ["name"] = new BString("myalbum", enc),
            ["piece length"] = new BNumber(262144),
            ["pieces"] = new BString(new byte[20]),
            ["length"] = new BNumber(9999),
            ["private"] = new BNumber(1),
            ["source"] = new BString("RED", enc),
        };

        var torrentDict = new BDictionary
        {
            ["info"] = infoDict,
            ["announce"] = new BString("https://tracker.example.com/announce", enc),
        };

        var torrent = TorrentParser.FromBDictionary(torrentDict);

        torrent.Info.Source.Should().Be("RED");

        // Re-encode and verify source is still in info dict
        var reEncoded = torrent.Info.ToBDictionary(enc);
        reEncoded.ContainsKey("source").Should().BeTrue();
        reEncoded.GetString("source").Should().Be("RED");
    }
}
