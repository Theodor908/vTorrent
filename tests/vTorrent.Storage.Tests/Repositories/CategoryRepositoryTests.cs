using FluentAssertions;
using vTorrent.Abstractions.Records;
using vTorrent.Storage.Tests.Helpers;
using Xunit;

namespace vTorrent.Storage.Tests.Repositories;

public class CategoryRepositoryTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task CreateAndGetCategory_RoundTrips()
    {
        var cat = await _fixture.Database.CreateCategoryAsync("Movies", "#ff0000", "/data/movies");

        cat.Id.Should().BeGreaterThan(0);
        cat.Name.Should().Be("Movies");

        var retrieved = await _fixture.Database.GetCategoryAsync(cat.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Color.Should().Be("#ff0000");
        retrieved.SavePath.Should().Be("/data/movies");
    }

    [Fact]
    public async Task GetCategoryByNameAsync_FindsExisting()
    {
        await _fixture.Database.CreateCategoryAsync("Music");

        var found = await _fixture.Database.GetCategoryByNameAsync("Music");
        found.Should().NotBeNull();
        found!.Name.Should().Be("Music");
    }

    [Fact]
    public async Task GetAllCategoriesAsync_ReturnsAll()
    {
        await _fixture.Database.CreateCategoryAsync("A");
        await _fixture.Database.CreateCategoryAsync("B");

        var all = await _fixture.Database.GetAllCategoriesAsync();
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateCategoryAsync_UpdatesFields()
    {
        var cat = await _fixture.Database.CreateCategoryAsync("Old", "#000");
        await _fixture.Database.UpdateCategoryAsync(cat.Id, "New", "#fff", "/new");

        var updated = await _fixture.Database.GetCategoryAsync(cat.Id);
        updated!.Name.Should().Be("New");
        updated.Color.Should().Be("#fff");
    }

    [Fact]
    public async Task DeleteCategoryAsync_RemovesCategory()
    {
        var cat = await _fixture.Database.CreateCategoryAsync("Delete Me");
        await _fixture.Database.DeleteCategoryAsync(cat.Id);

        var deleted = await _fixture.Database.GetCategoryAsync(cat.Id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task SetTorrentCategoryAsync_AssignsCategory()
    {
        var cat = await _fixture.Database.CreateCategoryAsync("Games");
        var torrent = new TorrentRecord
        {
            InfoHash = "cat_test_hash", Name = "game.iso", TotalSize = 1024,
            PieceCount = 1, PieceSize = 1024, SavePath = "/tmp",
            UserIntent = "Paused", AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        await _fixture.Database.InsertTorrentAsync(torrent);

        await _fixture.Database.SetTorrentCategoryAsync("cat_test_hash", cat.Id);

        var count = await _fixture.Database.GetTorrentCountByCategoryAsync(cat.Id);
        count.Should().Be(1);

        var torrents = await _fixture.Database.GetTorrentsByCategoryAsync(cat.Id);
        torrents.Should().HaveCount(1);
        torrents[0].InfoHash.Should().Be("cat_test_hash");
    }
}
