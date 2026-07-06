using FluentAssertions;
using vTorrent.Storage.Tests.Helpers;
using Xunit;

namespace vTorrent.Storage.Tests.Schema;

public class SchemaMigrationTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private async Task<List<string>> GetColumnNames(string tableName)
    {
        var columns = new List<string>();
        await using var cmd = _fixture.Connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));

        return columns;
    }

    private async Task<List<string>> GetTableNames()
    {
        var tables = new List<string>();
        await using var cmd = _fixture.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        return tables;
    }

    // Ported from TorrentDatabaseV2MigrationTests
    [Fact]
    public async Task Schema_HasInfoHashV2Column()
    {
        var columns = await GetColumnNames("torrents");
        columns.Should().Contain("info_hash_v2");
    }

    [Fact]
    public async Task Schema_HasTorrentVersionColumn()
    {
        var columns = await GetColumnNames("torrents");
        columns.Should().Contain("torrent_version");
    }

    // V2: Categories and tags
    [Fact]
    public async Task Schema_HasCategoriesTable()
    {
        var tables = await GetTableNames();
        tables.Should().Contain("categories");
    }

    [Fact]
    public async Task Schema_HasTagsTable()
    {
        var tables = await GetTableNames();
        tables.Should().Contain("tags");
    }

    [Fact]
    public async Task Schema_TorrentsHasCategoryIdColumn()
    {
        var columns = await GetColumnNames("torrents");
        columns.Should().Contain("category_id");
    }

    // V3: DHT tables and known_peers upgrade
    [Fact]
    public async Task Schema_HasDhtNodesTables()
    {
        var tables = await GetTableNames();
        tables.Should().Contain("dht_nodes");
        tables.Should().Contain("dht_state");
    }

    [Fact]
    public async Task Schema_KnownPeersHasTrustColumns()
    {
        var columns = await GetColumnNames("known_peers");
        columns.Should().Contain("trust_points");
        columns.Should().Contain("total_uploaded");
        columns.Should().Contain("total_downloaded");
    }

    // V4: First/last piece priority
    [Fact]
    public async Task Schema_HasFirstLastPiecePriorityColumn()
    {
        var columns = await GetColumnNames("torrents");
        columns.Should().Contain("first_last_piece_priority");
    }

    // V6: Orthogonal state columns
    [Fact]
    public async Task Schema_HasOrthogonalStateColumns()
    {
        var columns = await GetColumnNames("torrents");
        columns.Should().Contain("transfer_phase");
        columns.Should().Contain("file_operation");
        columns.Should().Contain("user_intent");
        columns.Should().Contain("health");
    }

    [Fact]
    public async Task Schema_VersionTableExists()
    {
        var tables = await GetTableNames();
        tables.Should().Contain("schema_version");
    }
}
