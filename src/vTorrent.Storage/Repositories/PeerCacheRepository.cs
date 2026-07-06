using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Records;

namespace vTorrent.Storage.Repositories;

/// <summary>
/// Known peers, banned peers, and DHT node persistence.
/// </summary>
internal class PeerCacheRepository
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;

    public PeerCacheRepository(SqliteConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
    }

    // Known peers

    public async Task<List<KnownPeerRecord>> GetKnownPeersAsync(string infoHash, int limit = 200)
    {
        const string sql = @"
            SELECT * FROM known_peers
            WHERE info_hash = @infoHash
            ORDER BY last_seen DESC, failed_count ASC
            LIMIT @limit";

        var result = await _connection.QueryAsync<KnownPeerRecord>(sql, new { infoHash, limit });
        return result.ToList();
    }

    public async Task SaveKnownPeersAsync(string infoHash, IEnumerable<KnownPeerRecord> peers)
    {
        const string sql = @"
            INSERT INTO known_peers (info_hash, ip, port, source, last_seen, last_connected,
                                     failed_count, trust_points, total_uploaded, total_downloaded)
            VALUES (@InfoHash, @Ip, @Port, @Source, @LastSeen, @LastConnected,
                    @FailedCount, @TrustPoints, @TotalUploaded, @TotalDownloaded)
            ON CONFLICT(info_hash, ip, port) DO UPDATE SET
                last_seen = MAX(excluded.last_seen, known_peers.last_seen),
                last_connected = MAX(excluded.last_connected, known_peers.last_connected),
                failed_count = excluded.failed_count,
                trust_points = excluded.trust_points,
                total_uploaded = MAX(excluded.total_uploaded, known_peers.total_uploaded),
                total_downloaded = MAX(excluded.total_downloaded, known_peers.total_downloaded),
                source = CASE WHEN excluded.last_seen > known_peers.last_seen
                              THEN excluded.source ELSE known_peers.source END";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var peer in peers)
        {
            peer.InfoHash = infoHash;
            if (!peer.LastSeen.HasValue)
                peer.LastSeen = now;
            await _connection.ExecuteAsync(sql, peer);
        }
    }

    public async Task<List<KnownPeerRecord>> GetKnownPeersForRestoreAsync(string infoHash, int limit = 500)
    {
        const string sql = @"
            SELECT * FROM known_peers
            WHERE info_hash = @infoHash AND failed_count < 3
            ORDER BY trust_points DESC, last_connected DESC, last_seen DESC
            LIMIT @limit";

        var result = await _connection.QueryAsync<KnownPeerRecord>(sql, new { infoHash, limit });
        return result.ToList();
    }

    public async Task PruneStaleKnownPeersAsync(string infoHash, int maxAgeDays = 7)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-maxAgeDays).ToUnixTimeSeconds();
        const string sql = "DELETE FROM known_peers WHERE info_hash = @infoHash AND last_seen < @cutoff";
        await _connection.ExecuteAsync(sql, new { infoHash, cutoff });
    }

    public async Task IncrementPeerFailCountAsync(string infoHash, string ip, int port)
    {
        const string sql = @"
            UPDATE known_peers
            SET failed_count = failed_count + 1
            WHERE info_hash = @infoHash AND ip = @ip AND port = @port";

        await _connection.ExecuteAsync(sql, new { infoHash, ip, port });
    }

    // Banning

    public async Task BanPeerAsync(string ip, string? reason = null)
    {
        const string sql = @"
            INSERT OR REPLACE INTO banned_peers (ip, reason, banned_at)
            VALUES (@ip, @reason, @bannedAt)";

        await _connection.ExecuteAsync(sql, new
        {
            ip,
            reason,
            bannedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    public async Task<bool> IsPeerBannedAsync(string ip)
    {
        const string sql = "SELECT COUNT(*) FROM banned_peers WHERE ip = @ip";
        return await _connection.ExecuteScalarAsync<int>(sql, new { ip }) > 0;
    }

    public async Task<List<BannedPeerRecord>> GetBannedPeersAsync()
    {
        const string sql = "SELECT * FROM banned_peers ORDER BY banned_at DESC";
        var result = await _connection.QueryAsync<BannedPeerRecord>(sql);
        return result.ToList();
    }

    public async Task UnbanPeerAsync(string ip)
    {
        const string sql = "DELETE FROM banned_peers WHERE ip = @ip";
        await _connection.ExecuteAsync(sql, new { ip });
    }

    // DHT

    public async Task SaveDhtNodesAsync(IEnumerable<DhtNodeRecord> nodes)
    {
        const string sql = @"
            INSERT INTO dht_nodes (node_id, ip, port, rtt_ms, last_seen)
            VALUES (@NodeId, @Ip, @Port, @RttMs, @LastSeen)
            ON CONFLICT(ip, port) DO UPDATE SET
                node_id = excluded.node_id,
                rtt_ms = excluded.rtt_ms,
                last_seen = excluded.last_seen";

        foreach (var node in nodes)
        {
            await _connection.ExecuteAsync(sql, node);
        }
    }

    public async Task<List<DhtNodeRecord>> GetDhtNodesAsync(int limit = 400, int maxAgeDays = 7)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-maxAgeDays).ToUnixTimeSeconds();
        const string sql = @"
            SELECT * FROM dht_nodes
            WHERE last_seen > @cutoff
            ORDER BY rtt_ms ASC, last_seen DESC
            LIMIT @limit";

        var result = await _connection.QueryAsync<DhtNodeRecord>(sql, new { cutoff, limit });
        return result.ToList();
    }

    public async Task PruneStaleDhtNodesAsync(int maxAgeDays = 7)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-maxAgeDays).ToUnixTimeSeconds();
        const string sql = "DELETE FROM dht_nodes WHERE last_seen < @cutoff";
        await _connection.ExecuteAsync(sql, new { cutoff });
    }

    public async Task SaveDhtStateAsync(string key, string value)
    {
        const string sql = @"
            INSERT INTO dht_state (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value";

        await _connection.ExecuteAsync(sql, new { key, value });
    }

    public async Task<string?> GetDhtStateAsync(string key)
    {
        const string sql = "SELECT value FROM dht_state WHERE key = @key";
        return await _connection.QuerySingleOrDefaultAsync<string>(sql, new { key });
    }
}
