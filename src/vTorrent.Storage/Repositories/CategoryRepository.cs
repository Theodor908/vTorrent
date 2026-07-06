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
/// Category CRUD and torrent-category assignment.
/// </summary>
internal class CategoryRepository
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;

    public CategoryRepository(SqliteConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task<List<Category>> GetAllCategoriesAsync()
    {
        const string sql = "SELECT * FROM categories ORDER BY sort_order, name";
        var result = await _connection.QueryAsync<Category>(sql);
        return result.ToList();
    }

    public async Task<Category?> GetCategoryAsync(int id)
    {
        const string sql = "SELECT * FROM categories WHERE id = @id";
        return await _connection.QueryFirstOrDefaultAsync<Category>(sql, new { id });
    }

    public async Task<Category?> GetCategoryByNameAsync(string name)
    {
        const string sql = "SELECT * FROM categories WHERE name = @name";
        return await _connection.QueryFirstOrDefaultAsync<Category>(sql, new { name });
    }

    public async Task<Category> CreateCategoryAsync(string name, string? color = null, string? savePath = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var maxOrder = await _connection.ExecuteScalarAsync<int?>(
            "SELECT MAX(sort_order) FROM categories") ?? -1;

        const string sql = @"
            INSERT INTO categories (name, color, save_path, sort_order, created_at, updated_at)
            VALUES (@name, @color, @savePath, @sortOrder, @now, @now);
            SELECT last_insert_rowid();";

        var id = await _connection.ExecuteScalarAsync<int>(sql, new
        {
            name,
            color,
            savePath,
            sortOrder = maxOrder + 1,
            now
        });

        return new Category
        {
            Id = id,
            Name = name,
            Color = color,
            SavePath = savePath,
            SortOrder = maxOrder + 1,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public async Task UpdateCategoryAsync(int id, string name, string? color, string? savePath)
    {
        const string sql = @"
            UPDATE categories
            SET name = @name, color = @color, save_path = @savePath, updated_at = @now
            WHERE id = @id";

        await _connection.ExecuteAsync(sql, new
        {
            id,
            name,
            color,
            savePath,
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    public async Task DeleteCategoryAsync(int id)
    {
        const string sql = "DELETE FROM categories WHERE id = @id";
        await _connection.ExecuteAsync(sql, new { id });
    }

    public async Task<int> GetTorrentCountByCategoryAsync(int categoryId)
    {
        const string sql = "SELECT COUNT(*) FROM torrents WHERE category_id = @categoryId";
        return await _connection.ExecuteScalarAsync<int>(sql, new { categoryId });
    }

    public async Task<List<TorrentRecord>> GetTorrentsByCategoryAsync(int categoryId)
    {
        const string sql = "SELECT * FROM torrents WHERE category_id = @categoryId ORDER BY added_at DESC";
        var result = await _connection.QueryAsync<TorrentRecord>(sql, new { categoryId });
        return result.ToList();
    }

    public async Task SetTorrentCategoryAsync(string infoHash, int? categoryId)
    {
        const string sql = @"
            UPDATE torrents
            SET category_id = @categoryId, updated_at = @now
            WHERE info_hash = @infoHash";

        await _connection.ExecuteAsync(sql, new
        {
            infoHash,
            categoryId,
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }
}
