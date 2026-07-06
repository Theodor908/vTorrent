using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Records;

namespace vTorrent.Storage.Repositories;

/// <summary>
/// Tag CRUD and torrent-tag assignment.
/// </summary>
internal class TagRepository
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;

    public TagRepository(SqliteConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task<List<Tag>> GetAllTagsAsync()
    {
        const string sql = "SELECT * FROM tags ORDER BY sort_order, name";
        var result = await _connection.QueryAsync<Tag>(sql);
        return result.ToList();
    }

    public async Task<Tag?> GetTagAsync(int id)
    {
        const string sql = "SELECT * FROM tags WHERE id = @id";
        return await _connection.QueryFirstOrDefaultAsync<Tag>(sql, new { id });
    }

    public async Task<Tag?> GetTagByNameAsync(string name)
    {
        const string sql = "SELECT * FROM tags WHERE name = @name";
        return await _connection.QueryFirstOrDefaultAsync<Tag>(sql, new { name });
    }

    public async Task<Tag> CreateTagAsync(string name, string? color = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var maxOrder = await _connection.ExecuteScalarAsync<int?>(
            "SELECT MAX(sort_order) FROM tags") ?? -1;

        const string sql = @"
            INSERT INTO tags (name, color, sort_order, created_at, updated_at)
            VALUES (@name, @color, @sortOrder, @now, @now);
            SELECT last_insert_rowid();";

        var id = await _connection.ExecuteScalarAsync<int>(sql, new
        {
            name,
            color,
            sortOrder = maxOrder + 1,
            now
        });

        return new Tag
        {
            Id = id,
            Name = name,
            Color = color,
            SortOrder = maxOrder + 1,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public async Task UpdateTagAsync(int id, string name, string? color)
    {
        const string sql = @"
            UPDATE tags
            SET name = @name, color = @color, updated_at = @now
            WHERE id = @id";

        await _connection.ExecuteAsync(sql, new
        {
            id,
            name,
            color,
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    public async Task DeleteTagAsync(int id)
    {
        const string sql = "DELETE FROM tags WHERE id = @id";
        await _connection.ExecuteAsync(sql, new { id });
    }

    public async Task<int> GetTorrentCountByTagAsync(int tagId)
    {
        const string sql = "SELECT COUNT(*) FROM torrent_tags WHERE tag_id = @tagId";
        return await _connection.ExecuteScalarAsync<int>(sql, new { tagId });
    }

    public async Task<List<TorrentRecord>> GetTorrentsByTagAsync(int tagId)
    {
        const string sql = @"
            SELECT t.* FROM torrents t
            INNER JOIN torrent_tags tt ON t.info_hash = tt.info_hash
            WHERE tt.tag_id = @tagId
            ORDER BY t.added_at DESC";

        var result = await _connection.QueryAsync<TorrentRecord>(sql, new { tagId });
        return result.ToList();
    }

    public async Task<List<Tag>> GetTorrentTagsAsync(string infoHash)
    {
        const string sql = @"
            SELECT t.* FROM tags t
            INNER JOIN torrent_tags tt ON t.id = tt.tag_id
            WHERE tt.info_hash = @infoHash
            ORDER BY t.sort_order, t.name";

        var result = await _connection.QueryAsync<Tag>(sql, new { infoHash });
        return result.ToList();
    }

    public async Task<Dictionary<string, List<Tag>>> GetAllTorrentTagsMappingAsync()
    {
        const string sql = @"
            SELECT tt.info_hash AS InfoHash, t.id AS Id, t.name AS Name, t.color AS Color,
                   t.sort_order AS SortOrder, t.created_at AS CreatedAt, t.updated_at AS UpdatedAt
            FROM tags t
            INNER JOIN torrent_tags tt ON t.id = tt.tag_id
            ORDER BY t.sort_order, t.name";

        var rows = await _connection.QueryAsync(sql);

        var result = new Dictionary<string, List<Tag>>();
        foreach (var row in rows)
        {
            string infoHash = (string)row.InfoHash;
            if (!result.TryGetValue(infoHash, out var list))
            {
                list = new List<Tag>();
                result[infoHash] = list;
            }
            list.Add(new Tag
            {
                Id = (int)(long)row.Id,
                Name = (string)row.Name,
                Color = row.Color == null ? null : (string)row.Color,
                SortOrder = (int)(long)row.SortOrder,
                CreatedAt = (long)row.CreatedAt,
                UpdatedAt = (long)row.UpdatedAt
            });
        }
        return result;
    }

    public async Task AddTorrentTagAsync(string infoHash, int tagId)
    {
        const string sql = @"
            INSERT OR IGNORE INTO torrent_tags (info_hash, tag_id)
            VALUES (@infoHash, @tagId)";

        await _connection.ExecuteAsync(sql, new { infoHash, tagId });
    }

    public async Task RemoveTorrentTagAsync(string infoHash, int tagId)
    {
        const string sql = "DELETE FROM torrent_tags WHERE info_hash = @infoHash AND tag_id = @tagId";
        await _connection.ExecuteAsync(sql, new { infoHash, tagId });
    }

    public async Task SetTorrentTagsAsync(string infoHash, IEnumerable<int> tagIds)
    {
        const string deleteSql = "DELETE FROM torrent_tags WHERE info_hash = @infoHash";
        await _connection.ExecuteAsync(deleteSql, new { infoHash });

        const string insertSql = @"
            INSERT INTO torrent_tags (info_hash, tag_id)
            VALUES (@infoHash, @tagId)";

        foreach (var tagId in tagIds)
        {
            await _connection.ExecuteAsync(insertSql, new { infoHash, tagId });
        }
    }
}
