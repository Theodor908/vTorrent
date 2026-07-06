using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using vTorrent.Core.ResumeData;
using vTorrent.Core.Session;
using vTorrent.Core.Settings;
using vTorrent.Storage;
using vTorrent.Core;
using vTorrent.Abstractions.Interfaces.Storage;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Records;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.IO;

namespace vTorrent.Core.Persistence;

/// <summary>
/// Persistence layer for session data.
/// Handles all database operations, resume data I/O, and settings.
/// Does NOT manage runtime state - that's the orchestrator's job.
/// </summary>
public class SessionPersistence : IAsyncDisposable
{
    #region Fields

    private static readonly JsonSerializerOptions _stateJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _dataDirectory;
    private readonly string _resumeDirectory;
    private readonly string _databasePath;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SessionPersistence> _logger;

    private readonly ISecureFileWiper _secureFileWiper;
    private readonly DeletionWorker _deletionWorker;

    private TorrentDatabase? _database;
    private SettingsManager? _settingsManager;
    private bool _isInitialized;
    private bool _isDisposed;

    #endregion

    #region Properties

    /// <summary>
    /// Data directory path
    /// </summary>
    public string DataDirectory => _dataDirectory;

    /// <summary>
    /// Resume data directory path
    /// </summary>
    public string ResumeDirectory => _resumeDirectory;

    /// <summary>
    /// Current global settings (read-only)
    /// </summary>
    public GlobalSettings Settings => _settingsManager?.Current ?? new GlobalSettings();

    /// <summary>
    /// Settings manager for read/write access to settings
    /// </summary>
    public SettingsManager? SettingsManager => _settingsManager;

    /// <summary>
    /// Whether persistence layer is initialized
    /// </summary>
    public bool IsInitialized => _isInitialized;

    /// <summary>
    /// Direct database access for advanced queries
    /// </summary>
    public TorrentDatabase? Database => _database;

    /// <summary>
    /// The shared SQLite connection for the embedded web server.
    /// </summary>
    public SqliteConnection? Connection => _database?.Connection;

    #endregion

    #region Constructor

    public SessionPersistence(string dataDirectory, ILoggerFactory loggerFactory,
        ISecureFileWiper secureFileWiper, DeletionWorker deletionWorker)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _resumeDirectory = Path.Combine(_dataDirectory, "resume");
        _databasePath = Path.Combine(_dataDirectory, "torrents.db");
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<SessionPersistence>();
        _secureFileWiper = secureFileWiper ?? throw new ArgumentNullException(nameof(secureFileWiper));
        _deletionWorker = deletionWorker ?? throw new ArgumentNullException(nameof(deletionWorker));
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Initialize persistence layer - database, settings, directories
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
            return;

        _logger.LogInformation("Initializing persistence layer...");

        // Ensure directories exist
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_resumeDirectory);

        // Initialize settings
        _settingsManager = new SettingsManager(_dataDirectory, _loggerFactory.CreateLogger<SettingsManager>());
        await _settingsManager.LoadAsync();
        _logger.LogDebug("Settings loaded");

        // Initialize database
        _database = new TorrentDatabase(_databasePath, _loggerFactory.CreateLogger<TorrentDatabase>());
        await _database.InitializeAsync();
        _logger.LogDebug("Database initialized");

        _isInitialized = true;
        _logger.LogInformation("Persistence layer initialized");
    }

    /// <summary>
    /// Graceful shutdown - flush pending writes
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _logger.LogInformation("Disposing persistence layer...");

        // Save settings if modified
        if (_settingsManager?.IsDirty == true)
        {
            await _settingsManager.SaveAsync();
        }

        // Close database
        if (_database != null)
        {
            await _database.DisposeAsync();
        }

        _logger.LogInformation("Persistence layer disposed");
    }

    #endregion

    #region Torrent Persistence

    /// <summary>
    /// Load all torrent records from database
    /// </summary>
    public async Task<IReadOnlyList<TorrentRecord>> LoadAllTorrentsAsync()
    {
        EnsureInitialized();
        return await _database!.GetAllTorrentsAsync();
    }

    /// <summary>
    /// Get a single torrent record
    /// </summary>
    public async Task<TorrentRecord?> GetTorrentAsync(string infoHash)
    {
        EnsureInitialized();
        return await _database!.GetTorrentAsync(infoHash);
    }

    /// <summary>
    /// Check if torrent exists in database
    /// </summary>
    public async Task<bool> TorrentExistsAsync(string infoHash)
    {
        EnsureInitialized();
        return await _database!.TorrentExistsAsync(infoHash);
    }

    /// <summary>
    /// Save a new torrent to database with trackers and files
    /// </summary>
    public async Task SaveNewTorrentAsync(
        TorrentRecord record,
        IEnumerable<(string Url, int Tier)> trackers,
        IEnumerable<FileRecord> files)
    {
        var result = await TrySaveNewTorrentAsync(record, trackers, files);
        if (!result)
        {
            throw new InvalidOperationException($"Torrent {record.InfoHash} already exists");
        }
    }

    /// <summary>
    /// Atomically save a new torrent, returns false if already exists.
    /// Uses INSERT OR IGNORE to prevent TOCTOU race conditions.
    /// </summary>
    public async Task<bool> TrySaveNewTorrentAsync(
        TorrentRecord record,
        IEnumerable<(string Url, int Tier)> trackers,
        IEnumerable<FileRecord> files)
    {
        EnsureInitialized();

        // Atomically try to insert - returns false if already exists
        var inserted = await _database!.TryInsertTorrentAsync(record);
        if (!inserted)
        {
            _logger.LogDebug("Torrent {InfoHash} already exists in database (atomic check)", record.InfoHash);
            return false;
        }

        // Only add trackers and files if insert succeeded
        if (trackers.Any())
        {
            await _database.AddTrackersAsync(record.InfoHash, trackers);
        }

        if (files.Any())
        {
            await _database.AddFilesAsync(record.InfoHash, files);
        }

        _logger.LogDebug("Saved new torrent: {InfoHash}", record.InfoHash);
        return true;
    }

    /// <summary>
    /// Update torrent intent in database
    /// </summary>
    public async Task UpdateTorrentIntentAsync(string infoHash, string intent)
    {
        EnsureInitialized();
        await _database!.UpdateTorrentIntentAsync(infoHash, intent);
        // Progress is updated via UpdateTorrentOnShutdownAsync or UpdateStatsAsync
    }

    /// <summary>
    /// Update torrent statistics including progress.
    /// This is the preferred method for periodic auto-saves.
    /// </summary>
    public async Task UpdateTorrentStatsAsync(string infoHash, TorrentStatsUpdate stats)
    {
        EnsureInitialized();
        await _database!.UpdateTorrentStatsAsync(infoHash, stats);
    }

    /// <summary>
    /// Mark torrent as completed (download finished, now seeding)
    /// </summary>
    public async Task MarkTorrentCompletedAsync(string infoHash)
    {
        EnsureInitialized();
        await _database!.MarkTorrentCompletedAsync(infoHash);
        _logger.LogDebug("Marked torrent as completed: {InfoHash}", infoHash);
    }

    /// <summary>
    /// Update torrent metadata after receiving it from peers (for magnet links).
    /// </summary>
    public async Task UpdateTorrentMetadataAsync(
        string infoHash,
        string name,
        long totalSize,
        int pieceCount,
        int pieceSize,
        int fileCount,
        string? torrentFilePath)
    {
        EnsureInitialized();
        await _database!.UpdateTorrentMetadataAsync(
            infoHash, name, totalSize, pieceCount, pieceSize, fileCount, torrentFilePath);
        _logger.LogDebug("Updated metadata for magnet link torrent: {InfoHash}", infoHash);
    }

    /// <summary>
    /// Save file records for a torrent.
    /// </summary>
    public async Task SaveFilesAsync(string infoHash, IEnumerable<FileRecord> files)
    {
        EnsureInitialized();
        await _database!.SaveFilesAsync(infoHash, files);
        _logger.LogDebug("Saved {Count} file records for {InfoHash}", files.Count(), infoHash);
    }

    /// <summary>
    /// Batch update multiple torrents (used during shutdown)
    /// </summary>
    public async Task BatchUpdateTorrentsAsync(IEnumerable<TorrentShutdownUpdate> updates)
    {
        EnsureInitialized();

        using var transaction = await _database!.BeginTransactionAsync();
        try
        {
            foreach (var update in updates)
            {
                await _database.UpdateTorrentOnShutdownAsync(update.InfoHash, new TorrentShutdownData
                {
                    Progress = update.Progress,
                    IsFinished = update.IsFinished,
                    IsSeed = update.IsSeed,
                    TotalUploaded = update.TotalUploaded,
                    TotalDownloaded = update.TotalDownloaded,
                    TotalPayloadUploaded = update.TotalPayloadUploaded,
                    TotalPayloadDownloaded = update.TotalPayloadDownloaded,
                    ActiveSeconds = update.ActiveSeconds,
                    SeedingSeconds = update.SeedingSeconds,
                    LastActiveAt = update.LastActiveAt,
                    LastUpload = update.LastUpload,
                    LastDownload = update.LastDownload,
                    // Orthogonal state dimensions
                    TransferPhase = update.TransferPhase,
                    FileOperation = update.FileOperation,
                    UserIntent = update.UserIntent,
                    Health = update.Health,
                    ErrorMessage = update.ErrorMessage
                });
            }

            await transaction.CommitAsync();
            _logger.LogDebug("Batch updated {Count} torrents", updates.Count());
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Delete torrent and associated data
    /// </summary>
    public async Task DeleteTorrentAsync(string infoHash, bool wipeMetadata = false)
    {
        EnsureInitialized();

        // Get queue position before deletion
        var record = await _database!.GetTorrentAsync(infoHash).ConfigureAwait(false);
        var queuePosition = record?.QueuePosition ?? 0;

        // Delete from database (cascades to trackers, files, peers)
        await _database.DeleteTorrentAsync(infoHash).ConfigureAwait(false);

        // Reorder queue
        await _database.ReorderQueueAfterRemovalAsync(queuePosition).ConfigureAwait(false);

        // Delete resume file
        var resumePath = GetResumeFilePath(infoHash);
        if (File.Exists(resumePath))
        {
            if (wipeMetadata)
                await _secureFileWiper.WipeFileAsync(resumePath).ConfigureAwait(false);
            else
                await _deletionWorker.DeleteFileAsync(resumePath).ConfigureAwait(false);
        }

        // Delete stored .torrent metadata file
        var torrentFilePath = Path.Combine(DataDirectory, "torrents", $"{infoHash}.torrent");
        if (File.Exists(torrentFilePath))
        {
            if (wipeMetadata)
                await _secureFileWiper.WipeFileAsync(torrentFilePath).ConfigureAwait(false);
            else
                await _deletionWorker.DeleteFileAsync(torrentFilePath).ConfigureAwait(false);
        }

        // Delete merkle tree file (BEP 52)
        var treePath = Path.Combine(_resumeDirectory, $"{infoHash}.tree");
        if (File.Exists(treePath))
        {
            if (wipeMetadata)
                await _secureFileWiper.WipeFileAsync(treePath).ConfigureAwait(false);
            else
                await _deletionWorker.DeleteFileAsync(treePath).ConfigureAwait(false);
        }

        // Delete per-torrent settings
        if (wipeMetadata)
        {
            // Wipe settings file directly — SettingsManager doesn't hold the wiper (SRP)
            var settingsPath = Path.Combine(DataDirectory, "settings", "torrents", $"{infoHash}.json");
            if (File.Exists(settingsPath))
                await _secureFileWiper.WipeFileAsync(settingsPath).ConfigureAwait(false);
        }
        else
        {
            await _settingsManager!.DeleteTorrentSettingsAsync(infoHash).ConfigureAwait(false);
        }

        _logger.LogDebug("Deleted torrent: {InfoHash}", infoHash);
    }

    #endregion

    #region Resume Data

    /// <summary>
    /// Load resume data (piece state) for a torrent
    /// </summary>
    public async Task<TorrentResumeData?> LoadResumeDataAsync(string infoHash)
    {
        var path = GetResumeFilePath(infoHash);
        if (!File.Exists(path))
            return null;

        try
        {
            return await ResumeDataSerializer.LoadAsync(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load resume data for {InfoHash}", infoHash);
            return null;
        }
    }

    /// <summary>
    /// Save resume data (piece state) for a torrent
    /// </summary>
    public async Task SaveResumeDataAsync(string infoHash, TorrentResumeData data)
    {
        var path = GetResumeFilePath(infoHash);
        data.LastSaved = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            await ResumeDataSerializer.SaveAsync(path, data);
            _logger.LogTrace("Saved resume data for {InfoHash}", infoHash);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save resume data for {InfoHash}", infoHash);
            throw;
        }
    }

    /// <summary>
    /// Delete resume data file
    /// </summary>
    public void DeleteResumeData(string infoHash)
    {
        var path = GetResumeFilePath(infoHash);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetResumeFilePath(string infoHash)
    {
        return Path.Combine(_resumeDirectory, $"{infoHash}.resume");
    }

    #endregion

    #region Queue Persistence

    /// <summary>
    /// Get next available queue position
    /// </summary>
    public async Task<int> GetNextQueuePositionAsync()
    {
        EnsureInitialized();
        return await _database!.GetNextQueuePositionAsync();
    }

    /// <summary>
    /// Update queue position for a torrent
    /// </summary>
    public async Task UpdateQueuePositionAsync(string infoHash, int position)
    {
        EnsureInitialized();
        await _database!.UpdateQueuePositionAsync(infoHash, position);
    }

    /// <summary>
    /// Batch update queue positions using transactional update.
    /// </summary>
    public async Task BatchUpdateQueuePositionsAsync(IEnumerable<(string InfoHash, int Position)> updates)
    {
        EnsureInitialized();
        var updateList = updates.Select(u => new QueuePositionUpdate(u.InfoHash, u.Position));
        await _database!.BatchUpdateQueuePositionsAsync(updateList);
    }

    #endregion

    #region Session State

    /// <summary>
    /// Load session state (DHT nodes, IP filter)
    /// </summary>
    public async Task<SessionState> LoadSessionStateAsync()
    {
        var path = Path.Combine(_dataDirectory, "session.state");
        return await SessionState.LoadAsync(path);
    }

    /// <summary>
    /// Save session state
    /// </summary>
    public async Task SaveSessionStateAsync(SessionState state)
    {
        var path = Path.Combine(_dataDirectory, "session.state");
        await state.SaveAsync(path);
        _logger.LogDebug("Saved session state");
    }

    #endregion

    #region Window State

    /// <summary>
    /// Load window state (position, size, maximized)
    /// </summary>
    public async Task<PersistedWindowState> LoadWindowStateAsync()
    {
        var path = Path.Combine(_dataDirectory, "window.json");
        if (!File.Exists(path))
            return new PersistedWindowState();
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var state = JsonSerializer.Deserialize<PersistedWindowState>(json, _stateJsonOptions) ?? new PersistedWindowState();
            _logger.LogDebug("Loaded window state: {Width}x{Height} at ({X},{Y})",
                state.Width, state.Height, state.X, state.Y);
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load window state, using defaults");
            return new PersistedWindowState();
        }
    }

    /// <summary>
    /// Save window state
    /// </summary>
    public async Task SaveWindowStateAsync(PersistedWindowState state)
    {
        var path = Path.Combine(_dataDirectory, "window.json");
        var json = JsonSerializer.Serialize(state, _stateJsonOptions);
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
        _logger.LogDebug("Saved window state");
    }

    #endregion

    #region View State

    /// <summary>
    /// Load view state (sort, filter, selection)
    /// </summary>
    public async Task<ViewState> LoadViewStateAsync()
    {
        var path = Path.Combine(_dataDirectory, "viewstate.json");
        if (!File.Exists(path))
            return new ViewState();
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var state = JsonSerializer.Deserialize<ViewState>(json, _stateJsonOptions) ?? new ViewState();
            _logger.LogDebug("Loaded view state: sort={Sort}, section={Section}", state.SortColumn, state.ActiveSection);
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load view state, using defaults");
            return new ViewState();
        }
    }

    /// <summary>
    /// Save view state
    /// </summary>
    public async Task SaveViewStateAsync(ViewState state)
    {
        var path = Path.Combine(_dataDirectory, "viewstate.json");
        var json = JsonSerializer.Serialize(state, _stateJsonOptions);
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
        _logger.LogDebug("Saved view state");
    }

    #endregion

    #region Settings

    /// <summary>
    /// Update and save global settings
    /// </summary>
    public async Task UpdateSettingsAsync(Action<GlobalSettings> update)
    {
        EnsureInitialized();
        await _settingsManager!.UpdateAndSaveAsync(update);
    }

    /// <summary>
    /// Get effective settings for a torrent (merged with global)
    /// </summary>
    public async Task<EffectiveTorrentSettings> GetEffectiveTorrentSettingsAsync(string infoHash)
    {
        EnsureInitialized();
        return await _settingsManager!.GetEffectiveSettingsAsync(infoHash);
    }

    /// <summary>
    /// Save per-torrent settings
    /// </summary>
    public async Task SaveTorrentSettingsAsync(TorrentSettings settings)
    {
        EnsureInitialized();
        await _settingsManager!.SaveTorrentSettingsAsync(settings);
    }

    #endregion

    #region Trackers

    /// <summary>
    /// Get trackers for a torrent
    /// </summary>
    public async Task<IReadOnlyList<TrackerRecord>> GetTrackersAsync(string infoHash)
    {
        EnsureInitialized();
        return await _database!.GetTrackersAsync(infoHash);
    }

    /// <summary>
    /// Add trackers to a torrent
    /// </summary>
    public async Task AddTrackersAsync(string infoHash, IEnumerable<(string Url, int Tier)> trackers)
    {
        EnsureInitialized();
        await _database!.AddTrackersAsync(infoHash, trackers);
    }

    /// <summary>
    /// Update tracker announce result
    /// </summary>
    public async Task UpdateTrackerAnnounceAsync(
        string infoHash,
        string url,
        long lastAnnounce,
        long nextAnnounce,
        int? seeders,
        int? leechers)
    {
        EnsureInitialized();
        await _database!.UpdateTrackerAnnounceAsync(infoHash, url, lastAnnounce, nextAnnounce, seeders, leechers);
    }

    public async Task RemoveTrackerAsync(string infoHash, string url)
    {
        EnsureInitialized();
        await _database!.RemoveTrackerAsync(infoHash, url);
    }

    #endregion

    #region Web Seeds

    /// <summary>
    /// Get web seeds for a torrent
    /// </summary>
    public async Task<IReadOnlyList<WebSeedRecord>> GetWebSeedsAsync(string infoHash)
    {
        EnsureInitialized();
        return await _database!.GetWebSeedsAsync(infoHash);
    }

    /// <summary>
    /// Add a web seed to a torrent
    /// </summary>
    public async Task AddWebSeedAsync(string infoHash, string url, string type)
    {
        EnsureInitialized();
        await _database!.AddWebSeedAsync(infoHash, url, type);
    }

    /// <summary>
    /// Remove a web seed from a torrent
    /// </summary>
    public async Task RemoveWebSeedAsync(string infoHash, string url)
    {
        EnsureInitialized();
        await _database!.RemoveWebSeedAsync(infoHash, url);
    }

    #endregion

    // Note: Peer persistence is handled directly by PeerCache -> TorrentDatabase

    #region Statistics History

    /// <summary>
    /// Record statistics snapshot for history/graphs
    /// </summary>
    public async Task RecordStatisticsSnapshotAsync(StatisticsSnapshot snapshot)
    {
        EnsureInitialized();
        await _database!.RecordStatisticsSnapshotAsync(
            snapshot.InfoHash,
            snapshot.DownloadRate,
            snapshot.UploadRate,
            snapshot.Downloaded,
            snapshot.Uploaded,
            snapshot.Peers,
            snapshot.Seeds);
    }

    /// <summary>
    /// Cleanup old statistics
    /// </summary>
    public async Task CleanupOldStatisticsAsync(int keepDays = 7)
    {
        EnsureInitialized();
        await _database!.CleanupOldStatisticsAsync(keepDays);
    }

    #endregion

    #region Categories

    /// <summary>
    /// Get all categories
    /// </summary>
    public async Task<IReadOnlyList<Category>> GetAllCategoriesAsync()
    {
        EnsureInitialized();
        return await _database!.GetAllCategoriesAsync();
    }

    /// <summary>
    /// Get a category by ID
    /// </summary>
    public async Task<Category?> GetCategoryAsync(int id)
    {
        EnsureInitialized();
        return await _database!.GetCategoryAsync(id);
    }

    /// <summary>
    /// Create a new category
    /// </summary>
    public async Task<Category> CreateCategoryAsync(string name, string? color = null, string? savePath = null)
    {
        EnsureInitialized();
        return await _database!.CreateCategoryAsync(name, color, savePath);
    }

    /// <summary>
    /// Update a category
    /// </summary>
    public async Task UpdateCategoryAsync(int id, string name, string? color, string? savePath)
    {
        EnsureInitialized();
        await _database!.UpdateCategoryAsync(id, name, color, savePath);
    }

    /// <summary>
    /// Delete a category
    /// </summary>
    public async Task DeleteCategoryAsync(int id)
    {
        EnsureInitialized();
        await _database!.DeleteCategoryAsync(id);
    }

    /// <summary>
    /// Get torrent count for a category
    /// </summary>
    public async Task<int> GetTorrentCountByCategoryAsync(int categoryId)
    {
        EnsureInitialized();
        return await _database!.GetTorrentCountByCategoryAsync(categoryId);
    }

    /// <summary>
    /// Set torrent category
    /// </summary>
    public async Task SetTorrentCategoryAsync(string infoHash, int? categoryId)
    {
        EnsureInitialized();
        await _database!.SetTorrentCategoryAsync(infoHash, categoryId);
    }

    #endregion

    #region Tags

    /// <summary>
    /// Get all tags
    /// </summary>
    public async Task<IReadOnlyList<Tag>> GetAllTagsAsync()
    {
        EnsureInitialized();
        return await _database!.GetAllTagsAsync();
    }

    /// <summary>
    /// Get a tag by ID
    /// </summary>
    public async Task<Tag?> GetTagAsync(int id)
    {
        EnsureInitialized();
        return await _database!.GetTagAsync(id);
    }

    /// <summary>
    /// Create a new tag
    /// </summary>
    public async Task<Tag> CreateTagAsync(string name, string? color = null)
    {
        EnsureInitialized();
        return await _database!.CreateTagAsync(name, color);
    }

    /// <summary>
    /// Update a tag
    /// </summary>
    public async Task UpdateTagAsync(int id, string name, string? color)
    {
        EnsureInitialized();
        await _database!.UpdateTagAsync(id, name, color);
    }

    /// <summary>
    /// Delete a tag
    /// </summary>
    public async Task DeleteTagAsync(int id)
    {
        EnsureInitialized();
        await _database!.DeleteTagAsync(id);
    }

    /// <summary>
    /// Get torrent count for a tag
    /// </summary>
    public async Task<int> GetTorrentCountByTagAsync(int tagId)
    {
        EnsureInitialized();
        return await _database!.GetTorrentCountByTagAsync(tagId);
    }

    /// <summary>
    /// Get tags for a torrent
    /// </summary>
    public async Task<IReadOnlyList<Tag>> GetTorrentTagsAsync(string infoHash)
    {
        EnsureInitialized();
        return await _database!.GetTorrentTagsAsync(infoHash);
    }

    /// <summary>
    /// Get all torrent-tag mappings in a single query, keyed by info hash.
    /// </summary>
    public async Task<Dictionary<string, List<Tag>>> GetAllTorrentTagsMappingAsync()
    {
        EnsureInitialized();
        return await _database!.GetAllTorrentTagsMappingAsync();
    }

    /// <summary>
    /// Add a tag to a torrent
    /// </summary>
    public async Task AddTorrentTagAsync(string infoHash, int tagId)
    {
        EnsureInitialized();
        await _database!.AddTorrentTagAsync(infoHash, tagId);
    }

    /// <summary>
    /// Remove a tag from a torrent
    /// </summary>
    public async Task RemoveTorrentTagAsync(string infoHash, int tagId)
    {
        EnsureInitialized();
        await _database!.RemoveTorrentTagAsync(infoHash, tagId);
    }

    /// <summary>
    /// Set all tags for a torrent (replaces existing)
    /// </summary>
    public async Task SetTorrentTagsAsync(string infoHash, IEnumerable<int> tagIds)
    {
        EnsureInitialized();
        await _database!.SetTorrentTagsAsync(infoHash, tagIds);
    }

    #endregion

    #region Helpers

    private void EnsureInitialized()
    {
        if (!_isInitialized)
            throw new InvalidOperationException("Persistence layer not initialized. Call InitializeAsync first.");
    }

    #endregion
}

#region Data Transfer Objects

// Note: TorrentStatsUpdate is defined in Storage/TorrentRecord.cs

/// <summary>
/// Full shutdown data for batch save.
/// Includes IsFinished and IsSeed flags to ensure correct state restoration
/// on restart (following libtorrent's model where these are orthogonal to paused state).
/// </summary>
public record TorrentShutdownUpdate(
    string InfoHash,
    double Progress,
    long TotalUploaded,
    long TotalDownloaded,
    long TotalPayloadUploaded,
    long TotalPayloadDownloaded,
    long ActiveSeconds,
    long SeedingSeconds,
    long LastActiveAt,
    long? LastUpload,
    long? LastDownload,
    bool IsFinished,
    bool IsSeed,
    // Orthogonal state dimensions (new)
    string? TransferPhase = null,
    string? FileOperation = null,
    string? UserIntent = null,
    string? Health = null,
    string? ErrorMessage = null
);

/// <summary>
/// Statistics snapshot for history
/// </summary>
public record StatisticsSnapshot(
    string? InfoHash,
    long Timestamp,
    int DownloadRate,
    int UploadRate,
    long Downloaded,
    long Uploaded,
    int Peers,
    int Seeds
);

#endregion
