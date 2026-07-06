using FluentAssertions;
using vTorrent.Bencode.Torrents;
using vTorrent.Core;
using Xunit;
using vTorrent.Core.Engine;

namespace vTorrent.Tests.Unit.Core;

public class TorrentCreatorTests : IDisposable
{
    private readonly string _tempDir;

    public TorrentCreatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vt_create_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch (IOException) { /* cleanup best-effort */ }
    }

    private string CreateTestFile(string name, int sizeBytes)
    {
        var path = Path.Combine(_tempDir, name);
        var data = new byte[sizeBytes];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public async Task CreateV1_ProducesValidTorrent()
    {
        var file = CreateTestFile("test.bin", 32768);

        var torrent = await TorrentCreator.CreateAsync(new TorrentCreateOptions
        {
            Name = "v1test",
            FilePaths = new[] { file },
            Mode = TorrentCreator.CreateMode.V1,
            PieceLength = 16384,
            Trackers = new[] { "http://tracker.example.com/announce" },
        });

        torrent.Info.Version.Should().Be(TorrentVersion.V1);
        torrent.Info.Pieces.Should().NotBeNull();
        torrent.Info.Pieces!.Count.Should().Be(2);
        torrent.Info.FileTreeV2.Should().BeNull();
        torrent.Info.PieceLength.Should().Be(16384);
    }

    [Fact]
    public async Task CreateV2_ProducesValidTorrent()
    {
        var file = CreateTestFile("test.bin", 32768);

        var torrent = await TorrentCreator.CreateAsync(new TorrentCreateOptions
        {
            Name = "v2test",
            FilePaths = new[] { file },
            Mode = TorrentCreator.CreateMode.V2,
            PieceLength = 16384,
            Trackers = new[] { "http://tracker.example.com/announce" },
        });

        torrent.Info.Version.Should().Be(TorrentVersion.V2);
        torrent.Info.Pieces.Should().BeNull();
        torrent.Info.FileTreeV2.Should().NotBeNull();
        torrent.Info.MetaVersion.Should().Be(2);
        torrent.PieceLayers.Should().NotBeNull();
        torrent.PieceLayers.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateHybrid_ProducesBothV1AndV2()
    {
        var file = CreateTestFile("test.bin", 32768);

        var torrent = await TorrentCreator.CreateAsync(new TorrentCreateOptions
        {
            Name = "hybrid",
            FilePaths = new[] { file },
            Mode = TorrentCreator.CreateMode.Hybrid,
            PieceLength = 16384,
            Trackers = new[] { "http://tracker.example.com/announce" },
        });

        torrent.Info.Version.Should().Be(TorrentVersion.Hybrid);
        torrent.Info.Pieces.Should().NotBeNull();
        torrent.Info.FileTreeV2.Should().NotBeNull();
        torrent.PieceLayers.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateV2_PieceLengthNotPowerOfTwo_Throws()
    {
        var file = CreateTestFile("test.bin", 1024);

        var act = () => TorrentCreator.CreateAsync(new TorrentCreateOptions
        {
            Name = "bad",
            FilePaths = new[] { file },
            Mode = TorrentCreator.CreateMode.V2,
            PieceLength = 30000,
        });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateV1_MultiFile_CorrectPieceCount()
    {
        var file1 = CreateTestFile("a.bin", 16384);
        var file2 = CreateTestFile("b.bin", 32768);

        var torrent = await TorrentCreator.CreateAsync(new TorrentCreateOptions
        {
            Name = "multifile",
            FilePaths = new[] { file1, file2 },
            Mode = TorrentCreator.CreateMode.V1,
            PieceLength = 16384,
            Trackers = new[] { "http://t.co/a" },
        });

        torrent.Info.Files.Should().HaveCount(2);
        torrent.Info.Pieces!.Count.Should().Be(3);
    }

    [Fact]
    public async Task Create_ReportsProgress()
    {
        var file = CreateTestFile("prog.bin", 65536);
        var progressReports = new List<TorrentCreator.TorrentCreateProgress>();

        var progress = new Progress<TorrentCreator.TorrentCreateProgress>(p =>
            progressReports.Add(p));

        await TorrentCreator.CreateAsync(new TorrentCreateOptions
        {
            Name = "progress",
            FilePaths = new[] { file },
            Mode = TorrentCreator.CreateMode.V1,
            PieceLength = 16384,
        }, progress);

        // Progress may be reported asynchronously, give it a moment
        await Task.Delay(50);
        progressReports.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Create_WithSource_SetsSourceOnTorrent()
    {
        var file = CreateTestFile("src.bin", 16384);

        var torrent = await TorrentCreator.CreateAsync(new TorrentCreateOptions
        {
            Name = "srctest",
            FilePaths = new[] { file },
            Mode = TorrentCreator.CreateMode.V1,
            PieceLength = 16384,
            Trackers = new[] { "http://tracker.red.example/announce" },
            Source = "RED",
        });

        torrent.Info.Source.Should().Be("RED");
    }

    [Fact]
    public async Task Create_WithUrlSeeds_SetsUrlListOnTorrent()
    {
        var file = CreateTestFile("ws.bin", 16384);

        var torrent = await TorrentCreator.CreateAsync(new TorrentCreateOptions
        {
            Name = "wstest",
            FilePaths = new[] { file },
            Mode = TorrentCreator.CreateMode.V1,
            PieceLength = 16384,
            Trackers = new[] { "http://t.co/a" },
            UrlSeeds = new[] { "http://seed.example.com/files/" },
        });

        torrent.UrlList.Should().HaveCount(1);
        torrent.UrlList![0].Should().Be("http://seed.example.com/files/");
    }

    [Fact]
    public async Task Create_WithHttpSeeds_SetsHttpSeedsOnTorrent()
    {
        var file = CreateTestFile("hs.bin", 16384);

        var torrent = await TorrentCreator.CreateAsync(new TorrentCreateOptions
        {
            Name = "hstest",
            FilePaths = new[] { file },
            Mode = TorrentCreator.CreateMode.V1,
            PieceLength = 16384,
            Trackers = new[] { "http://t.co/a" },
            HttpSeeds = new[] { "http://seed.example.com/seed.php" },
        });

        torrent.HttpSeeds.Should().HaveCount(1);
    }

    [Fact]
    public async Task Create_WithTrackerTiers_ProducesCorrectAnnounceList()
    {
        var file = CreateTestFile("tier.bin", 16384);

        var torrent = await TorrentCreator.CreateAsync(new TorrentCreateOptions
        {
            Name = "tiertest",
            FilePaths = new[] { file },
            Mode = TorrentCreator.CreateMode.V1,
            PieceLength = 16384,
            TrackerTiers = new IReadOnlyList<string>[]
            {
                new[] { "http://primary.example.com/announce" },
                new[] { "http://backup1.example.com/announce", "http://backup2.example.com/announce" },
            },
        });

        torrent.Announce.Should().Be("http://primary.example.com/announce");
        torrent.AnnounceList.Should().HaveCount(2);
        torrent.AnnounceList![0].Should().HaveCount(1);
        torrent.AnnounceList![1].Should().HaveCount(2);
    }

    [Fact]
    public async Task Create_Trackerless_Succeeds()
    {
        var file = CreateTestFile("dht.bin", 16384);

        var torrent = await TorrentCreator.CreateAsync(new TorrentCreateOptions
        {
            Name = "dhtonly",
            FilePaths = new[] { file },
            Mode = TorrentCreator.CreateMode.V1,
            PieceLength = 16384,
        });

        torrent.Announce.Should().BeEmpty();
        torrent.AnnounceList.Should().BeNull();
    }
}
