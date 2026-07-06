using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Records;

namespace vTorrent.Storage.Repositories;

/// <summary>
/// Queue position management.
/// </summary>
internal class QueueRepository
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;

    public QueueRepository(SqliteConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task<int> GetNextQueuePositionAsync()
    {
        const string sql = "SELECT COALESCE(MAX(queue_position), -1) + 1 FROM torrents";
        return await _connection.ExecuteScalarAsync<int>(sql);
    }

    public async Task UpdateQueuePositionAsync(string infoHash, int position)
    {
        const string sql = @"
            UPDATE torrents SET queue_position = @position, updated_at = @now
            WHERE info_hash = @infoHash";

        await _connection.ExecuteAsync(sql, new
        {
            infoHash,
            position,
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    public async Task ReorderQueueAfterRemovalAsync(int removedPosition)
    {
        const string sql = @"
            UPDATE torrents
            SET queue_position = queue_position - 1
            WHERE queue_position > @removedPosition";

        await _connection.ExecuteAsync(sql, new { removedPosition });
    }

    public async Task BatchUpdateQueuePositionsAsync(SqliteConnection connection, IEnumerable<QueuePositionUpdate> updates)
    {
        const string sql = @"
            UPDATE torrents SET queue_position = @Position, updated_at = @now
            WHERE info_hash = @InfoHash";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        try
        {
            foreach (var update in updates)
            {
                await connection.ExecuteAsync(sql, new
                {
                    update.InfoHash,
                    update.Position,
                    now
                }, transaction);
            }
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
