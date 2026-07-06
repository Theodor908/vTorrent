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
/// Statistics history recording, querying, and cleanup.
/// </summary>
internal class StatisticsRepository
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;

    public StatisticsRepository(SqliteConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task RecordStatisticsSnapshotAsync(string? infoHash, int downloadRate, int uploadRate,
        long downloaded, long uploaded, int peers, int seeds)
    {
        const string sql = @"
            INSERT INTO statistics_history (info_hash, timestamp, download_rate, upload_rate, downloaded, uploaded, peers, seeds)
            VALUES (@infoHash, @timestamp, @downloadRate, @uploadRate, @downloaded, @uploaded, @peers, @seeds)";

        await _connection.ExecuteAsync(sql, new
        {
            infoHash,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            downloadRate,
            uploadRate,
            downloaded,
            uploaded,
            peers,
            seeds
        });
    }

    public async Task<List<StatisticsSnapshotRecord>> GetStatisticsHistoryAsync(string? infoHash,
        long fromTimestamp, long toTimestamp, int limit = 1000)
    {
        const string sql = @"
            SELECT * FROM statistics_history
            WHERE (@infoHash IS NULL AND info_hash IS NULL) OR info_hash = @infoHash
            AND timestamp >= @fromTimestamp AND timestamp <= @toTimestamp
            ORDER BY timestamp DESC
            LIMIT @limit";

        var result = await _connection.QueryAsync<StatisticsSnapshotRecord>(sql,
            new { infoHash, fromTimestamp, toTimestamp, limit });
        return result.ToList();
    }

    public async Task CleanupOldStatisticsAsync(int keepDays = 7)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-keepDays).ToUnixTimeSeconds();
        const string sql = "DELETE FROM statistics_history WHERE timestamp < @cutoff";
        var deleted = await _connection.ExecuteAsync(sql, new { cutoff });

        if (deleted > 0)
        {
            _logger.LogDebug("Cleaned up {Count} old statistics records", deleted);
        }
    }
}
