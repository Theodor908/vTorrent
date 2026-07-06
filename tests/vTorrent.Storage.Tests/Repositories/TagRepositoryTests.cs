using FluentAssertions;
using vTorrent.Abstractions.Records;
using vTorrent.Storage.Tests.Helpers;
using Xunit;

namespace vTorrent.Storage.Tests.Repositories;

public class TagRepositoryTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task CreateAndGetTag_RoundTrips()
    {
        var tag = await _fixture.Database.CreateTagAsync("linux", "#00ff00");
        tag.Id.Should().BeGreaterThan(0);

        var retrieved = await _fixture.Database.GetTagAsync(tag.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("linux");
        retrieved.Color.Should().Be("#00ff00");
    }

    [Fact]
    public async Task AddAndGetTorrentTags_Works()
    {
        var tag1 = await _fixture.Database.CreateTagAsync("hd");
        var tag2 = await _fixture.Database.CreateTagAsync("remux");
        var torrent = new TorrentRecord
        {
            InfoHash = "tag_test", Name = "movie.mkv", TotalSize = 1024,
            PieceCount = 1, PieceSize = 1024, SavePath = "/tmp",
            UserIntent = "Paused", AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        await _fixture.Database.InsertTorrentAsync(torrent);

        await _fixture.Database.AddTorrentTagAsync("tag_test", tag1.Id);
        await _fixture.Database.AddTorrentTagAsync("tag_test", tag2.Id);

        var tags = await _fixture.Database.GetTorrentTagsAsync("tag_test");
        tags.Should().HaveCount(2);
    }

    [Fact]
    public async Task RemoveTorrentTagAsync_RemovesAssociation()
    {
        var tag = await _fixture.Database.CreateTagAsync("temp");
        var torrent = new TorrentRecord
        {
            InfoHash = "tag_rm", Name = "t", TotalSize = 1, PieceCount = 1,
            PieceSize = 1, SavePath = "/tmp", UserIntent = "Paused",
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        await _fixture.Database.InsertTorrentAsync(torrent);
        await _fixture.Database.AddTorrentTagAsync("tag_rm", tag.Id);
        await _fixture.Database.RemoveTorrentTagAsync("tag_rm", tag.Id);

        var tags = await _fixture.Database.GetTorrentTagsAsync("tag_rm");
        tags.Should().BeEmpty();
    }

    [Fact]
    public async Task SetTorrentTagsAsync_ReplacesAllTags()
    {
        var t1 = await _fixture.Database.CreateTagAsync("a");
        var t2 = await _fixture.Database.CreateTagAsync("b");
        var t3 = await _fixture.Database.CreateTagAsync("c");
        var torrent = new TorrentRecord
        {
            InfoHash = "tag_set", Name = "x", TotalSize = 1, PieceCount = 1,
            PieceSize = 1, SavePath = "/tmp", UserIntent = "Paused",
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        await _fixture.Database.InsertTorrentAsync(torrent);

        await _fixture.Database.SetTorrentTagsAsync("tag_set", new[] { t1.Id, t2.Id });
        await _fixture.Database.SetTorrentTagsAsync("tag_set", new[] { t3.Id });

        var tags = await _fixture.Database.GetTorrentTagsAsync("tag_set");
        tags.Should().HaveCount(1);
        tags[0].Id.Should().Be(t3.Id);
    }

    [Fact]
    public async Task DeleteTagAsync_RemovesTag()
    {
        var tag = await _fixture.Database.CreateTagAsync("doomed");
        await _fixture.Database.DeleteTagAsync(tag.Id);

        var deleted = await _fixture.Database.GetTagAsync(tag.Id);
        deleted.Should().BeNull();
    }
}
