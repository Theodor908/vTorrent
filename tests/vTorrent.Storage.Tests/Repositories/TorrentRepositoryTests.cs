using FluentAssertions;
using vTorrent.Abstractions.Records;
using vTorrent.Storage.Tests.Helpers;
using Xunit;

namespace vTorrent.Storage.Tests.Repositories;

public class TorrentRepositoryTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static TorrentRecord MakeTorrent(string infoHash = "abc123", string name = "test.txt") => new()
    {
        InfoHash = infoHash,
        Name = name,
        TotalSize = 1024,
        PieceCount = 1,
        PieceSize = 1024,
        SavePath = "/tmp/test",
        UserIntent = "Paused",
        AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        QueuePosition = 0
    };

    [Fact]
    public async Task InsertAndGetTorrent_RoundTrips()
    {
        var torrent = MakeTorrent();
        await _fixture.Database.InsertTorrentAsync(torrent);

        var retrieved = await _fixture.Database.GetTorrentAsync("abc123");
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("test.txt");
        retrieved.TotalSize.Should().Be(1024);
    }

    [Fact]
    public async Task TryInsertTorrentAsync_DuplicateReturnsFalse()
    {
        var torrent = MakeTorrent();
        var first = await _fixture.Database.TryInsertTorrentAsync(torrent);
        var second = await _fixture.Database.TryInsertTorrentAsync(torrent);

        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    [Fact]
    public async Task TorrentExistsAsync_ReturnsTrueForExisting()
    {
        await _fixture.Database.InsertTorrentAsync(MakeTorrent());

        (await _fixture.Database.TorrentExistsAsync("abc123")).Should().BeTrue();
        (await _fixture.Database.TorrentExistsAsync("nonexistent")).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateTorrentIntentAsync_UpdatesIntent()
    {
        await _fixture.Database.InsertTorrentAsync(MakeTorrent());
        await _fixture.Database.UpdateTorrentIntentAsync("abc123", "Active");

        var torrent = await _fixture.Database.GetTorrentAsync("abc123");
        torrent!.UserIntent.Should().Be("Active");
    }

    [Fact]
    public async Task UpdateTorrentProgressAsync_UpdatesProgress()
    {
        await _fixture.Database.InsertTorrentAsync(MakeTorrent());
        await _fixture.Database.UpdateTorrentProgressAsync("abc123", 0.75, false, false);

        var torrent = await _fixture.Database.GetTorrentAsync("abc123");
        torrent!.Progress.Should().BeApproximately(0.75, 0.001);
    }

    [Fact]
    public async Task MarkTorrentCompletedAsync_SetsFinished()
    {
        await _fixture.Database.InsertTorrentAsync(MakeTorrent());
        await _fixture.Database.MarkTorrentCompletedAsync("abc123");

        var torrent = await _fixture.Database.GetTorrentAsync("abc123");
        torrent!.IsFinished.Should().BeTrue();
        torrent.IsSeed.Should().BeTrue();
        torrent.Progress.Should().Be(1.0);
    }

    [Fact]
    public async Task DeleteTorrentAsync_RemovesTorrent()
    {
        await _fixture.Database.InsertTorrentAsync(MakeTorrent());
        await _fixture.Database.DeleteTorrentAsync("abc123");

        (await _fixture.Database.TorrentExistsAsync("abc123")).Should().BeFalse();
    }

    [Fact]
    public async Task GetAllTorrentsAsync_ReturnsAll()
    {
        await _fixture.Database.InsertTorrentAsync(MakeTorrent("hash1", "torrent1"));
        await _fixture.Database.InsertTorrentAsync(MakeTorrent("hash2", "torrent2"));

        var all = await _fixture.Database.GetAllTorrentsAsync();
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetTorrentsByIntentAsync_FiltersCorrectly()
    {
        await _fixture.Database.InsertTorrentAsync(MakeTorrent("h1", "a"));
        await _fixture.Database.InsertTorrentAsync(MakeTorrent("h2", "b"));
        await _fixture.Database.UpdateTorrentIntentAsync("h1", "Active");

        var active = await _fixture.Database.GetTorrentsByIntentAsync("Active");
        active.Should().HaveCount(1);
        active[0].InfoHash.Should().Be("h1");
    }

    [Fact]
    public async Task UpdateSavePathAsync_UpdatesPath()
    {
        await _fixture.Database.InsertTorrentAsync(MakeTorrent());
        await _fixture.Database.UpdateSavePathAsync("abc123", "/new/path");

        var torrent = await _fixture.Database.GetTorrentAsync("abc123");
        torrent!.SavePath.Should().Be("/new/path");
    }
}
