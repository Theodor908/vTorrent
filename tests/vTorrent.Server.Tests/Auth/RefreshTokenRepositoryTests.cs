using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using vTorrent.Server.Auth;

namespace vTorrent.Server.Tests.Auth;

public class RefreshTokenRepositoryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private RefreshTokenRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        await _connection.ExecuteAsync(@"
            CREATE TABLE refresh_tokens (
                id TEXT PRIMARY KEY,
                created_at INTEGER NOT NULL,
                expires_at INTEGER NOT NULL,
                revoked_at INTEGER,
                replaced_by TEXT
            );");
        _repo = new RefreshTokenRepository(_connection,
            NullLogger<RefreshTokenRepository>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task StoreAndGet_ReturnsStoredToken()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        await _repo.StoreAsync("token-1", expiresAt);

        var record = await _repo.GetAsync("token-1");
        record.Should().NotBeNull();
        record!.Id.Should().Be("token-1");
        record.ExpiresAt.Should().Be(expiresAt);
        record.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Get_NonExistent_ReturnsNull()
    {
        var record = await _repo.GetAsync("nonexistent");
        record.Should().BeNull();
    }

    [Fact]
    public async Task Revoke_ActiveToken_ReturnsTrue()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        await _repo.StoreAsync("token-2", expiresAt);

        var revoked = await _repo.RevokeAsync("token-2", replacedBy: "token-3");
        revoked.Should().BeTrue();

        var record = await _repo.GetAsync("token-2");
        record!.IsRevoked.Should().BeTrue();
        record.ReplacedBy.Should().Be("token-3");
    }

    [Fact]
    public async Task Revoke_AlreadyRevoked_ReturnsFalse()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        await _repo.StoreAsync("token-4", expiresAt);
        await _repo.RevokeAsync("token-4");

        var secondRevoke = await _repo.RevokeAsync("token-4");
        secondRevoke.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAll_RevokesAllActiveTokens()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        await _repo.StoreAsync("a1", expiresAt);
        await _repo.StoreAsync("a2", expiresAt);
        await _repo.StoreAsync("a3", expiresAt);

        await _repo.RevokeAllAsync();

        (await _repo.GetAsync("a1"))!.IsRevoked.Should().BeTrue();
        (await _repo.GetAsync("a2"))!.IsRevoked.Should().BeTrue();
        (await _repo.GetAsync("a3"))!.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task Cleanup_DeletesExpiredRevokedTokens()
    {
        var pastExpiry = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        await _repo.StoreAsync("old-1", pastExpiry);
        await _repo.RevokeAsync("old-1");

        var futureExpiry = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        await _repo.StoreAsync("active-1", futureExpiry);

        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _repo.CleanupAsync(cutoff);

        (await _repo.GetAsync("old-1")).Should().BeNull();
        (await _repo.GetAsync("active-1")).Should().NotBeNull();
    }

    [Fact]
    public async Task ExpiredToken_ReportsIsExpired()
    {
        var pastExpiry = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds();
        await _repo.StoreAsync("expired-1", pastExpiry);

        var record = await _repo.GetAsync("expired-1");
        record!.IsExpired.Should().BeTrue();
        record.IsActive.Should().BeFalse();
    }
}
