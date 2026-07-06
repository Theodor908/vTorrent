using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Storage;
using Xunit;

namespace vTorrent.Storage.Tests;

public class TorrentDatabaseInitTests
{
    [Fact]
    public async Task InitializeAsync_CanBeCalledOnFreshDatabase()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vt_test_{Guid.NewGuid():N}.db");
        var db = new TorrentDatabase(dbPath, new Mock<ILogger<TorrentDatabase>>().Object);

        var act = () => db.InitializeAsync();
        await act.Should().NotThrowAsync();

        await db.DisposeAsync();
        try { File.Delete(dbPath); } catch { }
    }

    [Fact]
    public async Task InitializeAsync_SecondCallOnExistingDb_SkipsMigration()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vt_test_{Guid.NewGuid():N}.db");

        // First init — creates schema
        var db1 = new TorrentDatabase(dbPath, new Mock<ILogger<TorrentDatabase>>().Object);
        await db1.InitializeAsync();
        await db1.DisposeAsync();

        // Second init — detects existing schema, no errors
        var db2 = new TorrentDatabase(dbPath, new Mock<ILogger<TorrentDatabase>>().Object);
        var act = () => db2.InitializeAsync();
        await act.Should().NotThrowAsync();

        await db2.DisposeAsync();
        try { File.Delete(dbPath); } catch { }
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"vt_test_{Guid.NewGuid():N}.db");
        var db = new TorrentDatabase(dbPath, new Mock<ILogger<TorrentDatabase>>().Object);
        await db.InitializeAsync();

        var act = async () =>
        {
            await db.DisposeAsync();
            await db.DisposeAsync();
        };
        await act.Should().NotThrowAsync();

        try { File.Delete(dbPath); } catch { }
    }
}
