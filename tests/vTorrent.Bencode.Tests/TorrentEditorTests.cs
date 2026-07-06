using FluentAssertions;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class TorrentEditorTests
{
    private BDictionary CreateSampleTorrentDict()
    {
        return new BDictionary
        {
            ["announce"] = new BString("http://tracker.example.com/announce"),
            ["announce-list"] = new BList
            {
                new BList { new BString("http://tracker.example.com/announce") },
                new BList { new BString("http://backup.example.com/announce") },
            },
            ["comment"] = new BString("test comment"),
            ["info"] = new BDictionary
            {
                ["name"] = new BString("testfile"),
                ["piece length"] = new BNumber(262144),
                ["pieces"] = new BString(new byte[20]),
                ["length"] = new BNumber(1024),
                ["source"] = new BString("PTP"),
                ["private"] = new BNumber(1),
            },
        };
    }

    [Fact]
    public void GetEditableMetadata_ExtractsAllFields()
    {
        var dict = CreateSampleTorrentDict();

        var metadata = TorrentEditor.GetEditableMetadata(dict);

        metadata.Name.Should().Be("testfile");
        metadata.Comment.Should().Be("test comment");
        metadata.Source.Should().Be("PTP");
        metadata.IsPrivate.Should().BeTrue();
        metadata.Trackers.Should().HaveCount(2);
        metadata.Trackers[0].Should().ContainSingle("http://tracker.example.com/announce");
    }

    [Fact]
    public void GetReadOnlyMetadata_ExtractsAllFields()
    {
        var dict = CreateSampleTorrentDict();

        var ro = TorrentEditor.GetReadOnlyMetadata(dict);

        ro.InfoHashV1.Should().NotBeNullOrEmpty();
        ro.InfoHashV2.Should().BeNull();
        ro.TotalSize.Should().Be(1024);
        ro.PieceSize.Should().Be(262144);
        ro.FileCount.Should().Be(1);
        ro.Format.Should().Be(TorrentVersion.V1);
    }

    [Fact]
    public void ApplyChanges_UpdatesComment()
    {
        var dict = CreateSampleTorrentDict();
        var metadata = TorrentEditor.GetEditableMetadata(dict);
        metadata.Comment = "new comment";

        TorrentEditor.ApplyChanges(dict, metadata);

        dict.GetString("comment").Should().Be("new comment");
    }

    [Fact]
    public void ApplyChanges_UpdatesSource_ChangesInfoHash()
    {
        var dict = CreateSampleTorrentDict();
        var hashBefore = TorrentEditor.RecalculateInfoHashes(dict);

        var metadata = TorrentEditor.GetEditableMetadata(dict);
        metadata.Source = "RED";
        TorrentEditor.ApplyChanges(dict, metadata);

        var hashAfter = TorrentEditor.RecalculateInfoHashes(dict);
        hashAfter.v1Hex.Should().NotBe(hashBefore.v1Hex);
    }

    [Fact]
    public void ApplyChanges_UpdatesTrackers()
    {
        var dict = CreateSampleTorrentDict();
        var metadata = TorrentEditor.GetEditableMetadata(dict);
        metadata.Trackers = new List<List<string>>
        {
            new() { "http://new-tracker.example.com/announce" },
        };

        TorrentEditor.ApplyChanges(dict, metadata);

        dict.GetString("announce").Should().Be("http://new-tracker.example.com/announce");
    }

    [Fact]
    public void ApplyChanges_PreservesUnknownKeys()
    {
        var dict = CreateSampleTorrentDict();
        dict["custom-key"] = new BString("custom-value");

        var metadata = TorrentEditor.GetEditableMetadata(dict);
        metadata.Comment = "edited";
        TorrentEditor.ApplyChanges(dict, metadata);

        dict.ContainsKey("custom-key").Should().BeTrue();
        dict.GetString("custom-key").Should().Be("custom-value");
    }

    [Fact]
    public void RecalculateInfoHashes_V1Torrent_ReturnsSha1()
    {
        var dict = CreateSampleTorrentDict();

        var (v1, v2) = TorrentEditor.RecalculateInfoHashes(dict);

        v1.Should().NotBeNullOrEmpty();
        v1.Should().HaveLength(40);
        v2.Should().BeNull();
    }
}
