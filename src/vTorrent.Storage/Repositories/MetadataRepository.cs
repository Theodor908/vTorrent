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
/// File and tracker metadata persistence.
/// </summary>
internal class MetadataRepository
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;

    public MetadataRepository(SqliteConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
    }

    // Files

    public async Task<List<FileRecord>> GetFilesAsync(string infoHash)
    {
        const string sql = "SELECT * FROM files WHERE info_hash = @infoHash ORDER BY file_index";
        var result = await _connection.QueryAsync<FileRecord>(sql, new { infoHash });
        return result.ToList();
    }

    public async Task AddFilesAsync(string infoHash, IEnumerable<FileRecord> files)
    {
        const string sql = @"
            INSERT INTO files (info_hash, file_index, path, size, priority)
            VALUES (@InfoHash, @FileIndex, @Path, @Size, @Priority)";

        foreach (var file in files)
        {
            file.InfoHash = infoHash;
            await _connection.ExecuteAsync(sql, file);
        }
    }

    public async Task UpdateFilePriorityAsync(string infoHash, int fileIndex, int priority)
    {
        const string sql = @"
            UPDATE files SET priority = @priority
            WHERE info_hash = @infoHash AND file_index = @fileIndex";

        await _connection.ExecuteAsync(sql, new { infoHash, fileIndex, priority });
    }

    public async Task UpdateFileProgressAsync(string infoHash, int fileIndex, double progress)
    {
        const string sql = @"
            UPDATE files SET progress = @progress
            WHERE info_hash = @infoHash AND file_index = @fileIndex";

        await _connection.ExecuteAsync(sql, new { infoHash, fileIndex, progress });
    }

    // Trackers

    public async Task<List<TrackerRecord>> GetTrackersAsync(string infoHash)
    {
        const string sql = "SELECT * FROM trackers WHERE info_hash = @infoHash ORDER BY tier, id";
        var result = await _connection.QueryAsync<TrackerRecord>(sql, new { infoHash });
        return result.ToList();
    }

    public async Task AddTrackersAsync(string infoHash, IEnumerable<(string url, int tier)> trackers)
    {
        const string sql = @"
            INSERT OR IGNORE INTO trackers (info_hash, url, tier)
            VALUES (@infoHash, @url, @tier)";

        foreach (var (url, tier) in trackers)
        {
            await _connection.ExecuteAsync(sql, new { infoHash, url, tier });
        }
    }

    public async Task UpdateTrackerAnnounceAsync(string infoHash, string url,
        long lastAnnounce, long nextAnnounce, int? seeders, int? leechers)
    {
        const string sql = @"
            UPDATE trackers
            SET last_announce = @lastAnnounce, next_announce = @nextAnnounce,
                seeders = @seeders, leechers = @leechers, status = 'working', message = NULL
            WHERE info_hash = @infoHash AND url = @url";

        await _connection.ExecuteAsync(sql, new { infoHash, url, lastAnnounce, nextAnnounce, seeders, leechers });
    }

    public async Task UpdateTrackerErrorAsync(string infoHash, string url, string errorMessage)
    {
        const string sql = @"
            UPDATE trackers
            SET status = 'error', message = @errorMessage
            WHERE info_hash = @infoHash AND url = @url";

        await _connection.ExecuteAsync(sql, new { infoHash, url, errorMessage });
    }

    public async Task RemoveTrackerAsync(string infoHash, string url)
    {
        const string sql = "DELETE FROM trackers WHERE info_hash = @infoHash AND url = @url";
        await _connection.ExecuteAsync(sql, new { infoHash, url });
    }

    // Web Seeds

    public async Task<List<WebSeedRecord>> GetWebSeedsAsync(string infoHash)
    {
        const string sql = "SELECT * FROM web_seeds WHERE info_hash = @infoHash";
        var result = await _connection.QueryAsync<WebSeedRecord>(sql, new { infoHash });
        return result.ToList();
    }

    public async Task AddWebSeedAsync(string infoHash, string url, string type)
    {
        const string sql = @"
            INSERT OR IGNORE INTO web_seeds (info_hash, url, type, added_at)
            VALUES (@infoHash, @url, @type, @addedAt)";
        await _connection.ExecuteAsync(sql, new { infoHash, url, type, addedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    public async Task RemoveWebSeedAsync(string infoHash, string url)
    {
        const string sql = "DELETE FROM web_seeds WHERE info_hash = @infoHash AND url = @url";
        await _connection.ExecuteAsync(sql, new { infoHash, url });
    }
}
