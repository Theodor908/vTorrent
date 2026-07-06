using FluentAssertions;
using vTorrent.Storage.Tests.Helpers;
using Xunit;

namespace vTorrent.Storage.Tests.Schema;

public class SchemaCreatorTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task FreshDatabase_CreatesAllExpectedTables()
    {
        var tables = new List<string>();
        await using var cmd = _fixture.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        tables.Should().Contain(new[]
        {
            "schema_version", "torrents", "trackers", "files",
            "known_peers", "banned_peers", "statistics_history",
            "categories", "tags", "torrent_tags",
            "dht_nodes", "dht_state"
        });
    }

    [Fact]
    public async Task FreshDatabase_CreatesIndexes()
    {
        var indexes = new List<string>();
        await using var cmd = _fixture.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%'";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            indexes.Add(reader.GetString(0));

        indexes.Should().Contain("idx_torrents_state");
        indexes.Should().Contain("idx_torrents_queue");
        indexes.Should().Contain("idx_trackers_infohash");
    }
}
