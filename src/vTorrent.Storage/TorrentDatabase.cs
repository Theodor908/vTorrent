using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Interfaces.Storage;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Records;
using vTorrent.Storage.Repositories;
using vTorrent.Storage.Schema;

namespace vTorrent.Storage;

/// <summary>
/// SQLite database for torrent persistence.
/// Facade that delegates to domain-specific repositories.
/// </summary>
public class TorrentDatabase : ITorrentDatabase
{
    private const int CurrentSchemaVersion = 10;

    private readonly string _dbPath;
    private readonly ILogger<TorrentDatabase> _logger;
    private SqliteConnection? _connection;

    /// <summary>
    /// The underlying SQLite connection, shared with the embedded web server.
    /// </summary>
    public SqliteConnection? Connection => _connection;

    private TorrentRepository? _torrents;
    private PeerCacheRepository? _peerCache;
    private MetadataRepository? _metadata;
    private CategoryRepository? _categories;
    private TagRepository? _tags;
    private QueueRepository? _queue;
    private StatisticsRepository? _statistics;

    public TorrentDatabase(string dbPath, ILogger<TorrentDatabase> logger)
    {
        _dbPath = dbPath;
        _logger = logger;
    }

    #region Initialization

    public async Task InitializeAsync()
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        _connection = new SqliteConnection($"Data Source={_dbPath}");
        await _connection.OpenAsync();

        await _connection.ExecuteAsync("PRAGMA foreign_keys = ON;");

        var tableExists = await _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_version'");

        if (tableExists == 0)
        {
            _logger.LogInformation("Creating new database schema...");
            await SchemaCreator.CreateSchemaAsync(_connection, _logger);
            await InsertSchemaVersionAsync(CurrentSchemaVersion);
        }
        else
        {
            var currentVersion = await GetSchemaVersionAsync();
            if (currentVersion < CurrentSchemaVersion)
            {
                _logger.LogInformation("Migrating database from v{Old} to v{New}",
                    currentVersion, CurrentSchemaVersion);
                await SchemaMigrator.MigrateAsync(_connection, _logger, currentVersion, CurrentSchemaVersion);
            }
        }

        // Initialize repositories
        _torrents = new TorrentRepository(_connection, _logger);
        _peerCache = new PeerCacheRepository(_connection, _logger);
        _metadata = new MetadataRepository(_connection, _logger);
        _categories = new CategoryRepository(_connection, _logger);
        _tags = new TagRepository(_connection, _logger);
        _queue = new QueueRepository(_connection, _logger);
        _statistics = new StatisticsRepository(_connection, _logger);

        _logger.LogInformation("Database initialized at {Path}", _dbPath);
    }

    private async Task<int> GetSchemaVersionAsync()
    {
        return await _connection!.ExecuteScalarAsync<int>(
            "SELECT MAX(version) FROM schema_version");
    }

    private async Task InsertSchemaVersionAsync(int version)
    {
        await _connection!.ExecuteAsync(
            "INSERT INTO schema_version (version, applied_at) VALUES (@version, @appliedAt)",
            new { version, appliedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    #endregion

    #region Torrent CRUD

    public Task<List<TorrentRecord>> GetAllTorrentsAsync()
        => _torrents!.GetAllTorrentsAsync();

    public Task<List<TorrentRecord>> GetTorrentsByIntentAsync(string intent)
        => _torrents!.GetTorrentsByIntentAsync(intent);

    public Task<TorrentRecord?> GetTorrentAsync(string infoHash)
        => _torrents!.GetTorrentAsync(infoHash);

    public Task<bool> TorrentExistsAsync(string infoHash)
        => _torrents!.TorrentExistsAsync(infoHash);

    public Task InsertTorrentAsync(TorrentRecord torrent)
        => _torrents!.InsertTorrentAsync(torrent);

    public Task<bool> TryInsertTorrentAsync(TorrentRecord torrent)
        => _torrents!.TryInsertTorrentAsync(torrent);

    public Task UpdateTorrentIntentAsync(string infoHash, string intent, string? errorMessage = null)
        => _torrents!.UpdateTorrentIntentAsync(infoHash, intent, errorMessage);

    public Task UpdateTorrentProgressAsync(string infoHash, double progress, bool isFinished, bool isSeed)
        => _torrents!.UpdateTorrentProgressAsync(infoHash, progress, isFinished, isSeed);

    public Task UpdateStatsAsync(string infoHash, long uploaded, long downloaded)
        => _torrents!.UpdateStatsAsync(infoHash, uploaded, downloaded);

    public Task UpdateTorrentStatsAsync(string infoHash, TorrentStatsUpdate stats)
        => _torrents!.UpdateTorrentStatsAsync(infoHash, stats);

    public Task UpdateTorrentOnShutdownAsync(string infoHash, TorrentShutdownData data)
        => _torrents!.UpdateTorrentOnShutdownAsync(infoHash, data);

    public Task MarkTorrentCompletedAsync(string infoHash)
        => _torrents!.MarkTorrentCompletedAsync(infoHash);

    public Task UpdateTorrentMetadataAsync(string infoHash, string name, long totalSize,
        int pieceCount, int pieceSize, int fileCount, string? torrentFilePath)
        => _torrents!.UpdateTorrentMetadataAsync(infoHash, name, totalSize, pieceCount, pieceSize, fileCount, torrentFilePath);

    public Task SaveFilesAsync(string infoHash, IEnumerable<FileRecord> files)
        => _torrents!.SaveFilesAsync(infoHash, files);

    public Task DeleteTorrentAsync(string infoHash)
        => _torrents!.DeleteTorrentAsync(infoHash);

    public Task UpdateTorrentSettingsAsync(string infoHash, int maxConnections, int maxUploads,
        int downloadLimit, int uploadLimit, bool sequentialDownload, bool firstLastPiecePriority = false)
        => _torrents!.UpdateTorrentSettingsAsync(infoHash, maxConnections, maxUploads, downloadLimit, uploadLimit, sequentialDownload, firstLastPiecePriority);

    public Task UpdateSavePathAsync(string infoHash, string newSavePath)
        => _torrents!.UpdateSavePathAsync(infoHash, newSavePath);

    #endregion

    #region Trackers

    public Task<List<TrackerRecord>> GetTrackersAsync(string infoHash)
        => _metadata!.GetTrackersAsync(infoHash);

    public Task AddTrackersAsync(string infoHash, IEnumerable<(string url, int tier)> trackers)
        => _metadata!.AddTrackersAsync(infoHash, trackers);

    public Task UpdateTrackerAnnounceAsync(string infoHash, string url,
        long lastAnnounce, long nextAnnounce, int? seeders, int? leechers)
        => _metadata!.UpdateTrackerAnnounceAsync(infoHash, url, lastAnnounce, nextAnnounce, seeders, leechers);

    public Task UpdateTrackerErrorAsync(string infoHash, string url, string errorMessage)
        => _metadata!.UpdateTrackerErrorAsync(infoHash, url, errorMessage);

    public Task RemoveTrackerAsync(string infoHash, string url)
        => _metadata!.RemoveTrackerAsync(infoHash, url);

    #endregion

    #region Web Seeds

    public Task<List<WebSeedRecord>> GetWebSeedsAsync(string infoHash)
        => _metadata!.GetWebSeedsAsync(infoHash);

    public Task AddWebSeedAsync(string infoHash, string url, string type)
        => _metadata!.AddWebSeedAsync(infoHash, url, type);

    public Task RemoveWebSeedAsync(string infoHash, string url)
        => _metadata!.RemoveWebSeedAsync(infoHash, url);

    #endregion

    #region Files

    public Task<List<FileRecord>> GetFilesAsync(string infoHash)
        => _metadata!.GetFilesAsync(infoHash);

    public Task AddFilesAsync(string infoHash, IEnumerable<FileRecord> files)
        => _metadata!.AddFilesAsync(infoHash, files);

    public Task UpdateFilePriorityAsync(string infoHash, int fileIndex, int priority)
        => _metadata!.UpdateFilePriorityAsync(infoHash, fileIndex, priority);

    public Task UpdateFileProgressAsync(string infoHash, int fileIndex, double progress)
        => _metadata!.UpdateFileProgressAsync(infoHash, fileIndex, progress);

    #endregion

    #region Peers

    public Task<List<KnownPeerRecord>> GetKnownPeersAsync(string infoHash, int limit = 200)
        => _peerCache!.GetKnownPeersAsync(infoHash, limit);

    public Task SaveKnownPeersAsync(string infoHash, IEnumerable<KnownPeerRecord> peers)
        => _peerCache!.SaveKnownPeersAsync(infoHash, peers);

    public Task<List<KnownPeerRecord>> GetKnownPeersForRestoreAsync(string infoHash, int limit = 500)
        => _peerCache!.GetKnownPeersForRestoreAsync(infoHash, limit);

    public Task PruneStaleKnownPeersAsync(string infoHash, int maxAgeDays = 7)
        => _peerCache!.PruneStaleKnownPeersAsync(infoHash, maxAgeDays);

    public Task IncrementPeerFailCountAsync(string infoHash, string ip, int port)
        => _peerCache!.IncrementPeerFailCountAsync(infoHash, ip, port);

    public Task BanPeerAsync(string ip, string? reason = null)
        => _peerCache!.BanPeerAsync(ip, reason);

    public Task<bool> IsPeerBannedAsync(string ip)
        => _peerCache!.IsPeerBannedAsync(ip);

    public Task<List<BannedPeerRecord>> GetBannedPeersAsync()
        => _peerCache!.GetBannedPeersAsync();

    public Task UnbanPeerAsync(string ip)
        => _peerCache!.UnbanPeerAsync(ip);

    #endregion

    #region DHT

    public Task SaveDhtNodesAsync(IEnumerable<DhtNodeRecord> nodes)
        => _peerCache!.SaveDhtNodesAsync(nodes);

    public Task<List<DhtNodeRecord>> GetDhtNodesAsync(int limit = 400, int maxAgeDays = 7)
        => _peerCache!.GetDhtNodesAsync(limit, maxAgeDays);

    public Task PruneStaleDhtNodesAsync(int maxAgeDays = 7)
        => _peerCache!.PruneStaleDhtNodesAsync(maxAgeDays);

    public Task SaveDhtStateAsync(string key, string value)
        => _peerCache!.SaveDhtStateAsync(key, value);

    public Task<string?> GetDhtStateAsync(string key)
        => _peerCache!.GetDhtStateAsync(key);

    #endregion

    #region Statistics

    public Task RecordStatisticsSnapshotAsync(string? infoHash, int downloadRate, int uploadRate,
        long downloaded, long uploaded, int peers, int seeds)
        => _statistics!.RecordStatisticsSnapshotAsync(infoHash, downloadRate, uploadRate, downloaded, uploaded, peers, seeds);

    public Task<List<StatisticsSnapshotRecord>> GetStatisticsHistoryAsync(string? infoHash,
        long fromTimestamp, long toTimestamp, int limit = 1000)
        => _statistics!.GetStatisticsHistoryAsync(infoHash, fromTimestamp, toTimestamp, limit);

    public Task CleanupOldStatisticsAsync(int keepDays = 7)
        => _statistics!.CleanupOldStatisticsAsync(keepDays);

    #endregion

    #region Queue

    public Task<int> GetNextQueuePositionAsync()
        => _queue!.GetNextQueuePositionAsync();

    public Task UpdateQueuePositionAsync(string infoHash, int position)
        => _queue!.UpdateQueuePositionAsync(infoHash, position);

    public Task ReorderQueueAfterRemovalAsync(int removedPosition)
        => _queue!.ReorderQueueAfterRemovalAsync(removedPosition);

    public Task BatchUpdateQueuePositionsAsync(IEnumerable<QueuePositionUpdate> updates)
        => _queue!.BatchUpdateQueuePositionsAsync(_connection!, updates);

    #endregion

    #region Categories

    public Task<List<Category>> GetAllCategoriesAsync()
        => _categories!.GetAllCategoriesAsync();

    public Task<Category?> GetCategoryAsync(int id)
        => _categories!.GetCategoryAsync(id);

    public Task<Category?> GetCategoryByNameAsync(string name)
        => _categories!.GetCategoryByNameAsync(name);

    public Task<Category> CreateCategoryAsync(string name, string? color = null, string? savePath = null)
        => _categories!.CreateCategoryAsync(name, color, savePath);

    public Task UpdateCategoryAsync(int id, string name, string? color, string? savePath)
        => _categories!.UpdateCategoryAsync(id, name, color, savePath);

    public Task DeleteCategoryAsync(int id)
        => _categories!.DeleteCategoryAsync(id);

    public Task<int> GetTorrentCountByCategoryAsync(int categoryId)
        => _categories!.GetTorrentCountByCategoryAsync(categoryId);

    public Task<List<TorrentRecord>> GetTorrentsByCategoryAsync(int categoryId)
        => _categories!.GetTorrentsByCategoryAsync(categoryId);

    public Task SetTorrentCategoryAsync(string infoHash, int? categoryId)
        => _categories!.SetTorrentCategoryAsync(infoHash, categoryId);

    #endregion

    #region Tags

    public Task<List<Tag>> GetAllTagsAsync()
        => _tags!.GetAllTagsAsync();

    public Task<Tag?> GetTagAsync(int id)
        => _tags!.GetTagAsync(id);

    public Task<Tag?> GetTagByNameAsync(string name)
        => _tags!.GetTagByNameAsync(name);

    public Task<Tag> CreateTagAsync(string name, string? color = null)
        => _tags!.CreateTagAsync(name, color);

    public Task UpdateTagAsync(int id, string name, string? color)
        => _tags!.UpdateTagAsync(id, name, color);

    public Task DeleteTagAsync(int id)
        => _tags!.DeleteTagAsync(id);

    public Task<int> GetTorrentCountByTagAsync(int tagId)
        => _tags!.GetTorrentCountByTagAsync(tagId);

    public Task<List<TorrentRecord>> GetTorrentsByTagAsync(int tagId)
        => _tags!.GetTorrentsByTagAsync(tagId);

    public Task<List<Tag>> GetTorrentTagsAsync(string infoHash)
        => _tags!.GetTorrentTagsAsync(infoHash);

    public Task<Dictionary<string, List<Tag>>> GetAllTorrentTagsMappingAsync()
        => _tags!.GetAllTorrentTagsMappingAsync();

    public Task AddTorrentTagAsync(string infoHash, int tagId)
        => _tags!.AddTorrentTagAsync(infoHash, tagId);

    public Task RemoveTorrentTagAsync(string infoHash, int tagId)
        => _tags!.RemoveTorrentTagAsync(infoHash, tagId);

    public Task SetTorrentTagsAsync(string infoHash, IEnumerable<int> tagIds)
        => _tags!.SetTorrentTagsAsync(infoHash, tagIds);

    #endregion

    #region Transactions

    public async Task<SqliteTransaction> BeginTransactionAsync()
    {
        return (SqliteTransaction)await _connection!.BeginTransactionAsync();
    }

    #endregion

    #region Disposal

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    #endregion
}
