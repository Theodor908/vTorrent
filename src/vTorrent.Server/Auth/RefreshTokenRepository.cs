using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace vTorrent.Server.Auth;

public class RefreshTokenRepository
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<RefreshTokenRepository> _logger;

    public RefreshTokenRepository(SqliteConnection connection, ILogger<RefreshTokenRepository> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task StoreAsync(string tokenId, long expiresAt)
    {
        await _connection.ExecuteAsync(
            "INSERT INTO refresh_tokens (id, created_at, expires_at) VALUES (@id, @createdAt, @expiresAt)",
            new { id = tokenId, createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), expiresAt });
    }

    public async Task<RefreshTokenRecord?> GetAsync(string tokenId)
    {
        return await _connection.QuerySingleOrDefaultAsync<RefreshTokenRecord>(
            "SELECT id AS Id, created_at AS CreatedAt, expires_at AS ExpiresAt, revoked_at AS RevokedAt, replaced_by AS ReplacedBy FROM refresh_tokens WHERE id = @id",
            new { id = tokenId });
    }

    /// <summary>
    /// Atomically revoke a token and record its replacement.
    /// Returns true if the token was active and successfully revoked.
    /// Returns false if already revoked (concurrent request — NOT a replay attack).
    /// </summary>
    public async Task<bool> RevokeAsync(string tokenId, string? replacedBy = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = await _connection.ExecuteAsync(
            "UPDATE refresh_tokens SET revoked_at = @now, replaced_by = @replacedBy WHERE id = @id AND revoked_at IS NULL",
            new { id = tokenId, now, replacedBy });
        return rows > 0;
    }

    /// <summary>
    /// Revoke ALL active tokens. Used on replay detection (single-user = full logout).
    /// </summary>
    public async Task RevokeAllAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = await _connection.ExecuteAsync(
            "UPDATE refresh_tokens SET revoked_at = @now WHERE revoked_at IS NULL",
            new { now });
        _logger.LogWarning("Revoked {Count} active refresh tokens (replay detection triggered)", rows);
    }

    /// <summary>
    /// Delete expired and revoked tokens older than the cutoff.
    /// </summary>
    public async Task CleanupAsync(long cutoffEpoch)
    {
        var rows = await _connection.ExecuteAsync(
            "DELETE FROM refresh_tokens WHERE expires_at < @cutoff AND revoked_at IS NOT NULL",
            new { cutoff = cutoffEpoch });
        if (rows > 0)
            _logger.LogDebug("Cleaned up {Count} expired/revoked refresh tokens", rows);
    }
}

public record RefreshTokenRecord(string Id, long CreatedAt, long ExpiresAt, long? RevokedAt, string? ReplacedBy)
{
    public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeSeconds() > ExpiresAt;
    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsActive => !IsExpired && !IsRevoked;
}
