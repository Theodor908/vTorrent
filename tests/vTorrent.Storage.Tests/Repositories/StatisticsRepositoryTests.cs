using FluentAssertions;
using vTorrent.Storage.Tests.Helpers;
using Xunit;

namespace vTorrent.Storage.Tests.Repositories;

public class StatisticsRepositoryTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task RecordAndGetStatistics_RoundTrips()
    {
        // Global stats (null infoHash) don't need a torrent row
        await _fixture.Database.RecordStatisticsSnapshotAsync(
            null, downloadRate: 1000, uploadRate: 500,
            downloaded: 1024, uploaded: 512, peers: 10, seeds: 5);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var history = await _fixture.Database.GetStatisticsHistoryAsync(null, now - 60, now + 60);
        history.Should().HaveCount(1);
        history[0].DownloadRate.Should().Be(1000);
    }

    [Fact]
    public async Task CleanupOldStatisticsAsync_RemovesStaleEntries()
    {
        await _fixture.Database.RecordStatisticsSnapshotAsync(
            null, 100, 50, 0, 0, 1, 1);

        // keepDays: -1 sets cutoff to tomorrow, guaranteeing all records are "stale"
        await _fixture.Database.CleanupOldStatisticsAsync(keepDays: -1);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var history = await _fixture.Database.GetStatisticsHistoryAsync(null, 0, now + 60);
        history.Should().BeEmpty();
    }
}
