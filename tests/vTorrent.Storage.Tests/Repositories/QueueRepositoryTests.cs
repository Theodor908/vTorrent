using FluentAssertions;
using vTorrent.Abstractions.Records;
using vTorrent.Storage.Tests.Helpers;
using Xunit;

namespace vTorrent.Storage.Tests.Repositories;

public class QueueRepositoryTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private async Task InsertTorrent(string hash, int queuePos)
    {
        await _fixture.Database.InsertTorrentAsync(new TorrentRecord
        {
            InfoHash = hash, Name = hash, TotalSize = 1, PieceCount = 1,
            PieceSize = 1, SavePath = "/tmp", UserIntent = "Paused",
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            QueuePosition = queuePos
        });
    }

    [Fact]
    public async Task GetNextQueuePositionAsync_ReturnsNextAvailable()
    {
        await InsertTorrent("q1", 0);
        await InsertTorrent("q2", 1);

        var next = await _fixture.Database.GetNextQueuePositionAsync();
        next.Should().Be(2);
    }

    [Fact]
    public async Task UpdateQueuePositionAsync_ChangesPosition()
    {
        await InsertTorrent("qp1", 0);
        await _fixture.Database.UpdateQueuePositionAsync("qp1", 5);

        var torrent = await _fixture.Database.GetTorrentAsync("qp1");
        torrent!.QueuePosition.Should().Be(5);
    }
}
