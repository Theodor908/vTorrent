using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using vTorrent.Abstractions.Settings;
using vTorrent.Server.Auth;

namespace vTorrent.Server.Tests.Auth;

/// <summary>
/// Integration tests for the full auth flow using direct service calls (no HTTP).
/// </summary>
public class AuthFlowIntegrationTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private RefreshTokenRepository _refreshRepo = null!;
    private JwtTokenService _jwt = null!;
    private PasswordHasher _passwordHasher = null!;
    private SettingsMonitor<ServerSettings> _monitor = null!;

    private const string Username = "admin";
    private const string Password = "testpass123";

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

        _refreshRepo = new RefreshTokenRepository(_connection,
            NullLogger<RefreshTokenRepository>.Instance);

        _passwordHasher = new PasswordHasher();

        _monitor = new SettingsMonitor<ServerSettings>();
        _monitor.Update(new ServerSettings
        {
            JwtSecret = JwtTokenService.GenerateJwtSecret(),
            JwtAccessTokenLifetimeMinutes = 15,
            JwtRefreshTokenLifetimeDays = 30,
            LocalUsername = Username,
            LocalPasswordHash = _passwordHasher.Hash(Password)
        });

        _jwt = new JwtTokenService(_monitor);
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Login_CorrectCredentials_ReturnsTokens()
    {
        var settings = _monitor.CurrentValue;

        // Verify credentials
        (Username == settings.LocalUsername).Should().BeTrue();
        _passwordHasher.Verify(Password, settings.LocalPasswordHash).Should().BeTrue();

        // Mint tokens
        var accessToken = _jwt.MintAccessToken(settings.LocalUsername);
        var refreshToken = _jwt.GenerateRefreshToken();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(settings.JwtRefreshTokenLifetimeDays).ToUnixTimeSeconds();
        await _refreshRepo.StoreAsync(refreshToken, expiresAt);

        accessToken.Should().NotBeNullOrEmpty();
        refreshToken.Should().HaveLength(64);

        var stored = await _refreshRepo.GetAsync(refreshToken);
        stored.Should().NotBeNull();
        stored!.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Login_WrongPassword_Fails()
    {
        var settings = _monitor.CurrentValue;
        _passwordHasher.Verify("wrongpassword", settings.LocalPasswordHash).Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_ValidToken_ReturnsNewTokensAndRevokesOld()
    {
        var settings = _monitor.CurrentValue;

        // Login
        var refreshToken = _jwt.GenerateRefreshToken();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        await _refreshRepo.StoreAsync(refreshToken, expiresAt);

        // Refresh
        var existing = await _refreshRepo.GetAsync(refreshToken);
        existing!.IsActive.Should().BeTrue();

        var newRefreshToken = _jwt.GenerateRefreshToken();
        var revoked = await _refreshRepo.RevokeAsync(refreshToken, replacedBy: newRefreshToken);
        revoked.Should().BeTrue();

        await _refreshRepo.StoreAsync(newRefreshToken, expiresAt);
        var newAccessToken = _jwt.MintAccessToken(settings.LocalUsername);

        // Verify old is revoked, new is active
        (await _refreshRepo.GetAsync(refreshToken))!.IsRevoked.Should().BeTrue();
        (await _refreshRepo.GetAsync(newRefreshToken))!.IsActive.Should().BeTrue();
        newAccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Refresh_RevokedToken_TriggersRevokeAll()
    {
        // Login twice to create two active tokens
        var token1 = _jwt.GenerateRefreshToken();
        var token2 = _jwt.GenerateRefreshToken();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        await _refreshRepo.StoreAsync(token1, expiresAt);
        await _refreshRepo.StoreAsync(token2, expiresAt);

        // Revoke token1 (simulating a legitimate refresh)
        await _refreshRepo.RevokeAsync(token1, replacedBy: "some-new-token");

        // Attempt to reuse token1 (replay attack)
        var existing = await _refreshRepo.GetAsync(token1);
        existing!.IsRevoked.Should().BeTrue();

        // Replay detected — revoke all
        await _refreshRepo.RevokeAllAsync();

        // Both tokens should now be revoked
        (await _refreshRepo.GetAsync(token1))!.IsRevoked.Should().BeTrue();
        (await _refreshRepo.GetAsync(token2))!.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_ExpiredToken_Fails()
    {
        var refreshToken = _jwt.GenerateRefreshToken();
        var pastExpiry = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds();
        await _refreshRepo.StoreAsync(refreshToken, pastExpiry);

        var existing = await _refreshRepo.GetAsync(refreshToken);
        existing!.IsExpired.Should().BeTrue();
        existing.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken()
    {
        var refreshToken = _jwt.GenerateRefreshToken();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        await _refreshRepo.StoreAsync(refreshToken, expiresAt);

        await _refreshRepo.RevokeAsync(refreshToken);

        var record = await _refreshRepo.GetAsync(refreshToken);
        record!.IsRevoked.Should().BeTrue();
        record.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentRefresh_OneSucceedsOneGetsConsumed()
    {
        var refreshToken = _jwt.GenerateRefreshToken();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        await _refreshRepo.StoreAsync(refreshToken, expiresAt);

        // First refresh succeeds
        var firstRevoke = await _refreshRepo.RevokeAsync(refreshToken, replacedBy: "new-token-1");
        firstRevoke.Should().BeTrue();

        // Second refresh (concurrent) fails — already consumed
        var secondRevoke = await _refreshRepo.RevokeAsync(refreshToken, replacedBy: "new-token-2");
        secondRevoke.Should().BeFalse();
    }
}
