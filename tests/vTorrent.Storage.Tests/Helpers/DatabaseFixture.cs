using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Storage;
using Xunit;

namespace vTorrent.Storage.Tests.Helpers;

/// <summary>
/// Creates a temporary on-disk SQLite database for integration tests.
/// Implements IAsyncLifetime so xUnit calls InitializeAsync/DisposeAsync per test class.
/// </summary>
public class DatabaseFixture : IAsyncLifetime
{
    public TorrentDatabase Database { get; private set; } = null!;
    public string DbPath { get; private set; } = null!;

    /// <summary>
    /// Exposes the internal SqliteConnection for schema introspection tests.
    /// </summary>
    public SqliteConnection Connection =>
        (SqliteConnection)typeof(TorrentDatabase)
            .GetField("_connection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(Database)!;

    public async Task InitializeAsync()
    {
        DbPath = Path.Combine(Path.GetTempPath(), $"vt_test_{Guid.NewGuid():N}.db");
        Database = new TorrentDatabase(DbPath, new Mock<ILogger<TorrentDatabase>>().Object);
        await Database.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await Database.DisposeAsync();
        try { if (File.Exists(DbPath)) File.Delete(DbPath); }
        catch (IOException) { /* WAL lock on Windows — best effort */ }
    }
}
