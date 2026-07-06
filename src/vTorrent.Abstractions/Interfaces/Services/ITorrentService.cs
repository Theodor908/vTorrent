using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Records;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Abstractions.Interfaces.Services;

/// <summary>
/// Platform-agnostic torrent service API.
/// Exposes engine and persistence operations using only Abstractions-level types.
/// Implemented by vTorrent.Core.Services.TorrentService; consumed by Desktop and future Server.
/// </summary>
public interface ITorrentService
{
    #region Lifecycle

    /// <summary>
    /// Whether the service has been initialized.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Current session statistics.
    /// </summary>
    SessionStatistics SessionStats { get; }

    #endregion

    #region Torrent Operations

    /// <summary>
    /// Add a torrent from a .torrent file.
    /// </summary>
    /// <returns>The info hash of the added torrent.</returns>
    Task<string> AddTorrentAsync(string torrentPath, string? savePath = null, bool startImmediately = true);

    /// <summary>
    /// Add a torrent with detailed options.
    /// </summary>
    /// <returns>The info hash of the added torrent.</returns>
    Task<string> AddTorrentAsync(string torrentPath, TorrentAddOptions options);

    /// <summary>
    /// Add a torrent from a magnet link.
    /// </summary>
    /// <returns>The info hash of the added torrent.</returns>
    Task<string> AddMagnetAsync(string magnetUri, string? savePath = null, bool startImmediately = true);

    /// <summary>
    /// Add a magnet link with detailed options.
    /// </summary>
    /// <returns>The info hash of the added torrent.</returns>
    Task<string> AddMagnetAsync(string magnetUri, TorrentAddOptions options);

    /// <summary>
    /// Pause a torrent.
    /// </summary>
    Task PauseTorrentAsync(string infoHash);

    /// <summary>
    /// Resume a paused torrent.
    /// </summary>
    Task ResumeTorrentAsync(string infoHash);

    /// <summary>
    /// Pause all active torrents.
    /// </summary>
    Task PauseAllAsync();

    /// <summary>
    /// Resume all paused torrents.
    /// </summary>
    Task ResumeAllAsync();

    /// <summary>
    /// Remove a torrent.
    /// </summary>
    Task<DeleteResult?> RemoveTorrentAsync(string infoHash, bool deleteFiles = false,
        bool secureWipe = false, bool wipeMetadata = false,
        IProgress<DeletionProgress>? progress = null);

    /// <summary>
    /// Delete remaining files in a torrent directory after removal.
    /// </summary>
    Task DeleteRemainingFilesAsync(string torrentDirectory, string savePath);

    /// <summary>
    /// Force recheck a torrent (re-verify all pieces).
    /// </summary>
    Task ForceRecheckAsync(string infoHash, bool resume = false);

    /// <summary>
    /// Force start a torrent, bypassing auto-management queue limits.
    /// </summary>
    Task ForceStartAsync(string infoHash);

    /// <summary>
    /// Toggle BEP 16 super-seeding mode for a torrent.
    /// </summary>
    Task ToggleSuperSeedingAsync(string infoHash);

    /// <summary>
    /// Force an immediate tracker reannounce for a torrent.
    /// </summary>
    Task ForceReannounceAsync(string infoHash);

    /// <summary>
    /// Change the save location of a torrent, moving all downloaded files.
    /// </summary>
    /// <returns>True if the move was successful.</returns>
    Task<bool> ChangeLocationAsync(string infoHash, string newSavePath);

    /// <summary>
    /// Apply per-torrent settings to a running engine.
    /// </summary>
    void ApplyTorrentSettings(string infoHash, TorrentSettings settings);

    /// <summary>
    /// Set file priorities for a torrent.
    /// </summary>
    Task SetFilePrioritiesAsync(string infoHash, IList<(int fileIndex, FilePriority priority)> priorities);

    /// <summary>
    /// Replace the tracker list for a running torrent (diff-based add/remove).
    /// </summary>
    Task<(int Added, int Removed)> UpdateTorrentTrackers(string infoHash, IList<string> trackerUrls);

    /// <summary>
    /// Replace the web seed list for a running torrent (diff-based add/remove).
    /// </summary>
    Task<(int Added, int Removed)> UpdateTorrentWebSeeds(string infoHash, IList<string> webSeedUrls);

    #endregion

    #region Queries

    /// <summary>
    /// Get all torrent snapshots.
    /// </summary>
    IReadOnlyList<TorrentSnapshot> GetTorrents();

    /// <summary>
    /// Get a single torrent snapshot by info hash.
    /// </summary>
    TorrentSnapshot? GetTorrent(string infoHash);

    /// <summary>
    /// Get detailed view of a torrent (trackers, peers, files).
    /// </summary>
    ManagedTorrentView? GetTorrentDetails(string infoHash);

    /// <summary>
    /// Get count of downloading torrents.
    /// </summary>
    int GetDownloadingCount();

    /// <summary>
    /// Get count of seeding torrents.
    /// </summary>
    int GetSeedingCount();

    /// <summary>
    /// Get count of paused torrents.
    /// </summary>
    int GetPausedCount();

    /// <summary>
    /// Get count of completed torrents.
    /// </summary>
    int GetCompletedCount();

    #endregion

    #region Queue

    /// <summary>
    /// Move a torrent to the top of its queue (highest priority).
    /// </summary>
    void SetQueuePositionTop(string infoHash);

    /// <summary>
    /// Move a torrent to the bottom of its queue (lowest priority).
    /// </summary>
    void SetQueuePositionBottom(string infoHash);

    /// <summary>
    /// Move a torrent up one position in its queue.
    /// </summary>
    void SetQueuePositionUp(string infoHash);

    /// <summary>
    /// Move a torrent down one position in its queue.
    /// </summary>
    void SetQueuePositionDown(string infoHash);

    #endregion

    #region Categories

    Task<IReadOnlyList<Category>> GetAllCategoriesAsync();
    Task<Category?> GetCategoryAsync(int id);
    Task<Category> CreateCategoryAsync(string name, string? color = null, string? savePath = null);
    Task UpdateCategoryAsync(int id, string name, string? color, string? savePath);
    Task DeleteCategoryAsync(int id);
    Task<int> GetTorrentCountByCategoryAsync(int categoryId);
    Task SetTorrentCategoryAsync(string infoHash, int? categoryId);

    #endregion

    #region Tags

    Task<IReadOnlyList<Tag>> GetAllTagsAsync();
    Task<Tag?> GetTagAsync(int id);
    Task<Tag> CreateTagAsync(string name, string? color = null);
    Task UpdateTagAsync(int id, string name, string? color);
    Task DeleteTagAsync(int id);
    Task<int> GetTorrentCountByTagAsync(int tagId);
    Task<IReadOnlyList<Tag>> GetTorrentTagsAsync(string infoHash);
    Task AddTorrentTagAsync(string infoHash, int tagId);
    Task RemoveTorrentTagAsync(string infoHash, int tagId);
    Task SetTorrentTagsAsync(string infoHash, IEnumerable<int> tagIds);

    #endregion

    #region DHT

    /// <summary>
    /// Whether DHT is currently running and ready.
    /// </summary>
    bool IsDhtRunning { get; }

    /// <summary>
    /// Whether DHT is enabled in settings.
    /// </summary>
    bool IsDhtEnabled { get; }

    /// <summary>
    /// Number of live DHT nodes in the routing table.
    /// </summary>
    int DhtNodeCount { get; }

    /// <summary>
    /// Toggle DHT on/off.
    /// </summary>
    Task ToggleDhtAsync();

    #endregion

    #region Settings

    /// <summary>
    /// Apply current settings to running torrents.
    /// </summary>
    Task ApplySettingsAsync();

    #endregion

    #region Events

    /// <summary>
    /// Raised when a torrent is added. Provides the info hash.
    /// </summary>
    event EventHandler<string>? TorrentAdded;

    /// <summary>
    /// Raised when a torrent is removed. Provides the info hash.
    /// </summary>
    event EventHandler<string>? TorrentRemoved;

    /// <summary>
    /// Raised when a torrent completes downloading. Provides the info hash.
    /// </summary>
    event EventHandler<string>? TorrentCompleted;

    /// <summary>
    /// Raised when session statistics are updated.
    /// </summary>
    event EventHandler<SessionStatistics>? StatsUpdated;

    /// <summary>
    /// Raised when a torrent changes status.
    /// </summary>
    event EventHandler<Abstractions.Events.TorrentStatusChangedEventArgs>? TorrentStatusChanged;

    /// <summary>
    /// Raised when a torrent encounters an error.
    /// </summary>
    event EventHandler<Abstractions.Events.TorrentErrorEventArgs>? TorrentError;

    /// <summary>
    /// Raised when DHT state changes.
    /// </summary>
    event EventHandler<Abstractions.Events.DhtStateChangedEventArgs>? DhtStateChanged;

    /// <summary>Raised when a category is created, updated, or deleted.</summary>
    event EventHandler<int>? CategoryChanged;

    /// <summary>Raised when a tag is created, updated, or deleted.</summary>
    event EventHandler<int>? TagChanged;

    /// <summary>Raised when the active profile changes (manual switch or scheduler).</summary>
    event EventHandler<string>? ProfileChanged;

    /// <summary>Raised when the schedule is enabled or disabled.</summary>
    event EventHandler<bool>? ScheduleToggled;

    #endregion

    #region Notifications

    /// <summary>
    /// Notify subscribers that the active profile has changed.
    /// </summary>
    void NotifyProfileChanged(string profileName);

    /// <summary>
    /// Notify subscribers that the schedule has been enabled or disabled.
    /// </summary>
    void NotifyScheduleToggled(bool enabled);

    #endregion

    #region Extended Queries

    /// <summary>
    /// Get the local piece bitfield as a boolean array.
    /// Returns null if torrent not found or engine not running.
    /// </summary>
    bool[]? GetPieceStates(string infoHash);

    /// <summary>
    /// Get per-peer transfer statistics for a torrent.
    /// Returns null if torrent not found or engine not running.
    /// </summary>
    IReadOnlyList<PeerStatsView>? GetPeerStats(string infoHash);

    #endregion
}
