using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Interfaces.Auth;
using vTorrent.Abstractions.Records;

namespace vTorrent.Server.Auth;

public class ApiKeyRepository : IApiKeyValidator
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<ApiKeyRepository> _logger;

    public ApiKeyRepository(SqliteConnection connection, ILogger<ApiKeyRepository> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    /// <summary>
    /// Generate a new API key, store its SHA-256 hash, and return the raw key (shown once).
    /// </summary>
    public async Task<(string RawKey, ApiKeyInfo Info)> CreateAsync(string label)
    {
        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var rawKey = Convert.ToHexString(rawBytes).ToLowerInvariant(); // 64-char hex
        var keyHash = HashKey(rawKey);
        var keyPrefix = rawKey[..8];
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await _connection.ExecuteAsync(
            "INSERT INTO api_keys (key_hash, key_prefix, label, created_at, last_used, revoked_at) VALUES (@keyHash, @keyPrefix, @label, @createdAt, NULL, NULL)",
            new { keyHash, keyPrefix, label, createdAt });

        _logger.LogInformation("Created API key with prefix {KeyPrefix} and label '{Label}'", keyPrefix, label);

        var info = new ApiKeyInfo(keyPrefix, label, createdAt, null, false);
        return (rawKey, info);
    }

    /// <summary>
    /// Returns true if the raw API key maps to a stored, non-revoked hash.
    /// </summary>
    public async Task<bool> ValidateAsync(string apiKey)
    {
        var keyHash = HashKey(apiKey);
        var row = await _connection.QuerySingleOrDefaultAsync<ApiKeyRow>(
            "SELECT key_hash AS KeyHash, key_prefix AS KeyPrefix, label AS Label, created_at AS CreatedAt, last_used AS LastUsed, revoked_at AS RevokedAt FROM api_keys WHERE key_hash = @keyHash",
            new { keyHash });

        return row is not null && row.RevokedAt is null;
    }

    /// <summary>
    /// Returns ApiKeyInfo for the given raw key if it exists and is not revoked.
    /// </summary>
    public async Task<ApiKeyInfo?> GetInfoAsync(string apiKey)
    {
        var keyHash = HashKey(apiKey);
        var row = await _connection.QuerySingleOrDefaultAsync<ApiKeyRow>(
            "SELECT key_hash AS KeyHash, key_prefix AS KeyPrefix, label AS Label, created_at AS CreatedAt, last_used AS LastUsed, revoked_at AS RevokedAt FROM api_keys WHERE key_hash = @keyHash",
            new { keyHash });

        if (row is null || row.RevokedAt is not null)
            return null;

        return new ApiKeyInfo(row.KeyPrefix, row.Label, row.CreatedAt, row.LastUsed, false);
    }

    /// <summary>
    /// Returns all API keys (active and revoked), newest first.
    /// </summary>
    public async Task<IEnumerable<ApiKeyInfo>> ListAsync()
    {
        var rows = await _connection.QueryAsync<ApiKeyRow>(
            "SELECT key_hash AS KeyHash, key_prefix AS KeyPrefix, label AS Label, created_at AS CreatedAt, last_used AS LastUsed, revoked_at AS RevokedAt FROM api_keys ORDER BY created_at DESC");

        var results = new List<ApiKeyInfo>();
        foreach (var row in rows)
            results.Add(new ApiKeyInfo(row.KeyPrefix, row.Label, row.CreatedAt, row.LastUsed, row.RevokedAt is not null));
        return results;
    }

    /// <summary>
    /// Soft-deletes (revokes) all non-revoked keys matching the given prefix.
    /// Returns true if at least one row was updated.
    /// </summary>
    public async Task<bool> RevokeByPrefixAsync(string keyPrefix)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = await _connection.ExecuteAsync(
            "UPDATE api_keys SET revoked_at = @now WHERE key_prefix = @keyPrefix AND revoked_at IS NULL",
            new { keyPrefix, now });

        if (rows > 0)
            _logger.LogInformation("Revoked {Count} API key(s) with prefix {KeyPrefix}", rows, keyPrefix);

        return rows > 0;
    }

    /// <summary>
    /// Updates the last_used timestamp for a given key hash.
    /// </summary>
    public async Task UpdateLastUsedAsync(string keyHash, long timestamp)
    {
        await _connection.ExecuteAsync(
            "UPDATE api_keys SET last_used = @timestamp WHERE key_hash = @keyHash",
            new { keyHash, timestamp });
    }

    /// <summary>
    /// Permanently deletes revoked keys whose revoked_at is older than the cutoff epoch.
    /// </summary>
    public async Task CleanupRevokedAsync(long cutoffEpoch)
    {
        var rows = await _connection.ExecuteAsync(
            "DELETE FROM api_keys WHERE revoked_at IS NOT NULL AND revoked_at < @cutoff",
            new { cutoff = cutoffEpoch });

        if (rows > 0)
            _logger.LogDebug("Cleaned up {Count} revoked API key(s) older than epoch {Cutoff}", rows, cutoffEpoch);
    }

    /// <summary>
    /// Computes the SHA-256 hash of a raw API key and returns it as a lowercase hex string.
    /// </summary>
    internal static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record ApiKeyRow(
        string KeyHash,
        string KeyPrefix,
        string Label,
        long CreatedAt,
        long? LastUsed,
        long? RevokedAt);
}
