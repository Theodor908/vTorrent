using FluentAssertions;
using vTorrent.Abstractions.Records;
using vTorrent.Storage.Tests.Helpers;
using Xunit;

namespace vTorrent.Storage.Tests.Repositories;

public class MetadataRepositoryTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private async Task InsertTorrent(string hash = "meta_test")
    {
        await _fixture.Database.InsertTorrentAsync(new TorrentRecord
        {
            InfoHash = hash, Name = "test", TotalSize = 1024, PieceCount = 1,
            PieceSize = 1024, SavePath = "/tmp", UserIntent = "Paused",
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    [Fact]
    public async Task AddAndGetTrackers_RoundTrips()
    {
        await InsertTorrent();
        var trackers = new[] { ("http://tracker1.com/announce", 0), ("http://tracker2.com/announce", 1) };
        await _fixture.Database.AddTrackersAsync("meta_test", trackers);

        var retrieved = await _fixture.Database.GetTrackersAsync("meta_test");
        retrieved.Should().HaveCount(2);
        retrieved.Should().Contain(t => t.Url == "http://tracker1.com/announce");
    }

    [Fact]
    public async Task AddAndGetFiles_RoundTrips()
    {
        await InsertTorrent();
        var files = new[]
        {
            new FileRecord { InfoHash = "meta_test", FileIndex = 0, Path = "file1.txt", Size = 512, Priority = 4 },
            new FileRecord { InfoHash = "meta_test", FileIndex = 1, Path = "file2.txt", Size = 512, Priority = 4 }
        };
        await _fixture.Database.AddFilesAsync("meta_test", files);

        var retrieved = await _fixture.Database.GetFilesAsync("meta_test");
        retrieved.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateFilePriorityAsync_ChangesPriority()
    {
        await InsertTorrent();
        var files = new[]
        {
            new FileRecord { InfoHash = "meta_test", FileIndex = 0, Path = "f.txt", Size = 100, Priority = 4 }
        };
        await _fixture.Database.AddFilesAsync("meta_test", files);

        await _fixture.Database.UpdateFilePriorityAsync("meta_test", 0, 7);

        var retrieved = await _fixture.Database.GetFilesAsync("meta_test");
        retrieved[0].Priority.Should().Be(7);
    }
}
