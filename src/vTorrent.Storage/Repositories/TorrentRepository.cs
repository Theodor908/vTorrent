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
/// All torrent CRUD operations.
/// </summary>
internal class TorrentRepository
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;

    public TorrentRepository(SqliteConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task<List<TorrentRecord>> GetAllTorrentsAsync()
    {
        const string sql = "SELECT * FROM torrents ORDER BY added_at DESC";
        var result = await _connection.QueryAsync<TorrentRecord>(sql);
        return result.ToList();
    }

    public async Task<List<TorrentRecord>> GetTorrentsByIntentAsync(string intent)
    {
        const string sql = "SELECT * FROM torrents WHERE user_intent = @intent ORDER BY queue_position";
        var result = await _connection.QueryAsync<TorrentRecord>(sql, new { intent });
        return result.ToList();
    }

    public async Task<TorrentRecord?> GetTorrentAsync(string infoHash)
    {
        const string sql = "SELECT * FROM torrents WHERE info_hash = @infoHash";
        return await _connection.QueryFirstOrDefaultAsync<TorrentRecord>(sql, new { infoHash });
    }

    public async Task<bool> TorrentExistsAsync(string infoHash)
    {
        const string sql = "SELECT COUNT(*) FROM torrents WHERE info_hash = @infoHash";
        return await _connection.ExecuteScalarAsync<int>(sql, new { infoHash }) > 0;
    }

    public async Task InsertTorrentAsync(TorrentRecord torrent)
    {
        var result = await TryInsertTorrentAsync(torrent);
        if (!result)
        {
            throw new InvalidOperationException($"Torrent with info hash {torrent.InfoHash} already exists");
        }
    }

    public async Task<bool> TryInsertTorrentAsync(TorrentRecord torrent)
    {
        const string sql = @"
            INSERT OR IGNORE INTO torrents (
                info_hash, name, comment, created_by,
                total_size, piece_count, piece_size, file_count, is_private,
                save_path, torrent_file_path,
                user_intent, progress, is_finished, is_seed,
                total_uploaded, total_downloaded,
                added_at, queue_position,
                max_connections, max_uploads, download_limit, upload_limit,
                sequential_download, auto_managed,
                is_magnet_link, magnet_uri,
                created_at, updated_at
            ) VALUES (
                @InfoHash, @Name, @Comment, @CreatedBy,
                @TotalSize, @PieceCount, @PieceSize, @FileCount, @IsPrivate,
                @SavePath, @TorrentFilePath,
                @UserIntent, @Progress, @IsFinished, @IsSeed,
                @TotalUploaded, @TotalDownloaded,
                @AddedAt, @QueuePosition,
                @MaxConnections, @MaxUploads, @DownloadLimit, @UploadLimit,
                @SequentialDownload, @AutoManaged,
                @IsMagnetLink, @MagnetUri,
                @CreatedAt, @UpdatedAt
            )";

        torrent.CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        torrent.UpdatedAt = torrent.CreatedAt;

        var rowsAffected = await _connection.ExecuteAsync(sql, torrent);
        return rowsAffected > 0;
    }

    public async Task UpdateTorrentIntentAsync(string infoHash, string intent, string? errorMessage = null)
    {
        const string sql = @"
            UPDATE torrents
            SET user_intent = @intent, error_message = @errorMessage, updated_at = @now
            WHERE info_hash = @infoHash";

        await _connection.ExecuteAsync(sql, new
        {
            infoHash,
            intent,
            errorMessage,
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    public async Task UpdateTorrentProgressAsync(string infoHash, double progress, bool isFinished, bool isSeed)
    {
        const string sql = @"
            UPDATE torrents
            SET progress = @progress, is_finished = @isFinished, is_seed = @isSeed, updated_at = @now
            WHERE info_hash = @infoHash";

        await _connection.ExecuteAsync(sql, new
        {
            infoHash,
            progress,
            isFinished,
            isSeed,
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    public async Task UpdateStatsAsync(string infoHash, long uploaded, long downloaded)
    {
        const string sql = @"
            UPDATE torrents
            SET total_uploaded = @uploaded, total_downloaded = @downloaded, updated_at = @now
            WHERE info_hash = @infoHash";

        await _connection.ExecuteAsync(sql, new
        {
            infoHash,
            uploaded,
            downloaded,
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    public async Task UpdateTorrentStatsAsync(string infoHash, TorrentStatsUpdate stats)
    {
        const string sql = @"
            UPDATE torrents
            SET total_uploaded = @TotalUploaded,
                total_downloaded = @TotalDownloaded,
                total_payload_uploaded = @TotalPayloadUploaded,
                total_payload_downloaded = @TotalPayloadDownloaded,
                progress = @Progress,
                is_finished = @IsFinished,
                is_seed = @IsSeed,
                active_seconds = @ActiveSeconds,
                seeding_seconds = @SeedingSeconds,
                last_active_at = @now,
                updated_at = @now
            WHERE info_hash = @infoHash";

        await _connection.ExecuteAsync(sql, new
        {
            infoHash,
            stats.TotalUploaded,
            stats.TotalDownloaded,
            stats.TotalPayloadUploaded,
            stats.TotalPayloadDownloaded,
            stats.Progress,
            stats.IsFinished,
            stats.IsSeed,
            stats.ActiveSeconds,
            stats.SeedingSeconds,
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    public async Task UpdateTorrentOnShutdownAsync(string infoHash, TorrentShutdownData data)
    {
        const string sql = @"
            UPDATE torrents
            SET state = @UserIntent,
                progress = @Progress,
                is_finished = @IsFinished,
                is_seed = @IsSeed,
                total_uploaded = @TotalUploaded,
                total_downloaded = @TotalDownloaded,
                total_payload_uploaded = @TotalPayloadUploaded,
                total_payload_downloaded = @TotalPayloadDownloaded,
                active_seconds = @ActiveSeconds,
                seeding_seconds = @SeedingSeconds,
                last_active_at = @LastActiveAt,
                last_upload = COALESCE(@LastUpload, last_upload),
                last_download = COALESCE(@LastDownload, last_download),
                transfer_phase = @TransferPhase,
                file_operation = @FileOperation,
                user_intent = @UserIntent,
                health = @Health,
                error_message = @ErrorMessage,
                updated_at = @now
            WHERE info_hash = @infoHash";

        await _connection.ExecuteAsync(sql, new
        {
            infoHash,
            data.Progress,
            data.IsFinished,
            data.IsSeed,
            data.TotalUploaded,
            data.TotalDownloaded,
            data.TotalPayloadUploaded,
            data.TotalPayloadDownloaded,
            data.ActiveSeconds,
            data.SeedingSeconds,
            data.LastActiveAt,
            data.LastUpload,
            data.LastDownload,
            data.TransferPhase,
            data.FileOperation,
            data.UserIntent,
            data.Health,
            data.ErrorMessage,
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    public async Task MarkTorrentCompletedAsync(string infoHash)
    {
        const string sql = @"
            UPDATE torrents
            SET is_finished = 1, is_seed = 1, progress = 1.0,
                completed_at = @now, updated_at = @now
            WHERE info_hash = @infoHash";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _connection.ExecuteAsync(sql, new { infoHash, now });
    }

    public async Task UpdateTorrentMetadataAsync(
        string infoHash, string name, long totalSize, int pieceCount,
        int pieceSize, int fileCount, string? torrentFilePath)
    {
        const string sql = @"
            UPDATE torrents
            SET name = @name,
                total_size = @totalSize,
                piece_count = @pieceCount,
                piece_size = @pieceSize,
                file_count = @fileCount,
                torrent_file_path = @torrentFilePath,
                updated_at = @now
            WHERE info_hash = @infoHash";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _connection.ExecuteAsync(sql, new
        {
            infoHash,
            name,
            totalSize,
            pieceCount,
            pieceSize,
            fileCount,
            torrentFilePath,
            now
        });
    }

    public async Task SaveFilesAsync(string infoHash, IEnumerable<FileRecord> files)
    {
        await _connection.ExecuteAsync(
            "DELETE FROM files WHERE info_hash = @infoHash",
            new { infoHash });

        const string insertSql = @"
            INSERT INTO files (info_hash, file_index, path, size, priority, progress)
            VALUES (@InfoHash, @FileIndex, @Path, @Size, @Priority, @Progress)";

        await _connection.ExecuteAsync(insertSql, files);
    }

    public async Task DeleteTorrentAsync(string infoHash)
    {
        const string sql = "DELETE FROM torrents WHERE info_hash = @infoHash";
        await _connection.ExecuteAsync(sql, new { infoHash });
    }

    public async Task UpdateTorrentSettingsAsync(string infoHash, int maxConnections, int maxUploads,
        int downloadLimit, int uploadLimit, bool sequentialDownload, bool firstLastPiecePriority = false)
    {
        const string sql = @"
            UPDATE torrents
            SET max_connections = @maxConnections, max_uploads = @maxUploads,
                download_limit = @downloadLimit, upload_limit = @uploadLimit,
                sequential_download = @sequentialDownload,
                first_last_piece_priority = @firstLastPiecePriority,
                updated_at = @now
            WHERE info_hash = @infoHash";

        await _connection.ExecuteAsync(sql, new
        {
            infoHash,
            maxConnections,
            maxUploads,
            downloadLimit,
            uploadLimit,
            sequentialDownload,
            firstLastPiecePriority,
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }

    public async Task UpdateSavePathAsync(string infoHash, string newSavePath)
    {
        const string sql = @"
            UPDATE torrents
            SET save_path = @newSavePath, updated_at = @now
            WHERE info_hash = @infoHash";

        await _connection.ExecuteAsync(sql, new
        {
            infoHash,
            newSavePath,
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
    }
}
