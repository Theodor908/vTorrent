using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Interfaces.Storage;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Records;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Events;
using vTorrent.Core.Orchestration;
using vTorrent.Core.Session;

namespace vTorrent.Core.Services;

/// <summary>
/// Platform-agnostic torrent service wrapping TorrentOrchestrator.
/// Exposes only Abstractions-level types so Desktop and future Server
/// can consume the same API without depending on Core internals.
/// </summary>
public class TorrentService : ITorrentService
{
    private readonly TorrentOrchestrator _orchestrator;

    public TorrentService(TorrentOrchestrator orchestrator)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

        // Inject self into the ProfileScheduler to break the circular dependency.
        // The orchestrator holds a ProfileScheduler that needs ITorrentService to fire
        // ProfileChanged events; it cannot receive it via constructor DI because
        // TorrentService depends on TorrentOrchestrator, so we inject after construction.
        _orchestrator.InjectTorrentService(this);

        // Wire orchestrator events to ITorrentService events
        _orchestrator.TorrentAdded += OnOrchestratorTorrentAdded;
        _orchestrator.TorrentRemoved += OnOrchestratorTorrentRemoved;
        _orchestrator.TorrentCompleted += OnOrchestratorTorrentCompleted;
        _orchestrator.StatisticsUpdated += OnOrchestratorStatisticsUpdated;
        _orchestrator.TorrentStatusChanged += OnOrchestratorTorrentStatusChanged;
        _orchestrator.TorrentFailed += OnOrchestratorTorrentFailed;
        _orchestrator.DhtStateChanged += OnOrchestratorDhtStateChanged;
    }

    #region Lifecycle

    public bool IsInitialized => _orchestrator.IsInitialized;

    public SessionStatistics SessionStats => _orchestrator.Statistics;

    #endregion

    #region Torrent Operations

    public async Task<string> AddTorrentAsync(string torrentPath, string? savePath = null, bool startImmediately = true)
    {
        var handle = await _orchestrator.AddTorrentAsync(torrentPath, savePath, startImmediately);
        return handle.InfoHash;
    }

    public async Task<string> AddTorrentAsync(string torrentPath, TorrentAddOptions options)
    {
        // Add torrent WITHOUT starting immediately — we need to set file priorities
        // on the ManagedTorrent BEFORE the engine initializes.
        var infoHash = await AddTorrentAsync(torrentPath, options.SavePath, startImmediately: false);
        await ApplyAddOptionsAsync(infoHash, options);

        if (options.StartImmediately)
        {
            await _orchestrator.StartTorrentAsync(infoHash).ConfigureAwait(false);
        }

        return infoHash;
    }

    public async Task<string> AddMagnetAsync(string magnetUri, string? savePath = null, bool startImmediately = true)
    {
        var handle = await _orchestrator.AddMagnetLinkAsync(magnetUri, savePath, startImmediately);
        return handle.InfoHash;
    }

    public async Task<string> AddMagnetAsync(string magnetUri, TorrentAddOptions options)
    {
        var infoHash = await AddMagnetAsync(magnetUri, options.SavePath, options.StartImmediately);
        await ApplyAddOptionsAsync(infoHash, options);
        return infoHash;
    }

    public async Task PauseTorrentAsync(string infoHash)
    {
        await _orchestrator.PauseTorrentAsync(infoHash).ConfigureAwait(false);
    }

    public async Task ResumeTorrentAsync(string infoHash)
    {
        await _orchestrator.StartTorrentAsync(infoHash).ConfigureAwait(false);
    }

    public async Task PauseAllAsync()
    {
        await _orchestrator.PauseAllAsync().ConfigureAwait(false);
    }

    public async Task ResumeAllAsync()
    {
        await _orchestrator.ResumeAllAsync().ConfigureAwait(false);
    }

    public async Task<DeleteResult?> RemoveTorrentAsync(string infoHash, bool deleteFiles = false,
        bool secureWipe = false, bool wipeMetadata = false,
        IProgress<DeletionProgress>? progress = null)
    {
        var coreResult = await _orchestrator.RemoveTorrentAsync(infoHash, deleteFiles, secureWipe, wipeMetadata, progress)
            .ConfigureAwait(false);

        if (coreResult == null) return null;

        return new DeleteResult
        {
            HasExtraFiles = coreResult.HasExtraFiles,
            ExtraFiles = coreResult.ExtraFiles,
            TorrentDirectory = coreResult.TorrentDirectory,
            SavePath = coreResult.SavePath,
        };
    }

    public async Task DeleteRemainingFilesAsync(string torrentDirectory, string savePath)
    {
        await _orchestrator.DeleteRemainingFilesAsync(torrentDirectory, savePath).ConfigureAwait(false);
    }

    public async Task ForceRecheckAsync(string infoHash, bool resume = false)
    {
        await _orchestrator.ForceRecheckAsync(infoHash, resume: resume).ConfigureAwait(false);
    }

    public async Task ForceStartAsync(string infoHash)
    {
        await _orchestrator.ForceStartAsync(infoHash).ConfigureAwait(false);
    }

    public async Task ToggleSuperSeedingAsync(string infoHash)
    {
        await _orchestrator.ToggleSuperSeedingAsync(infoHash).ConfigureAwait(false);
    }

    public async Task ForceReannounceAsync(string infoHash)
    {
        var managed = _orchestrator.TorrentsInternal.Find(infoHash);
        var trackerManager = managed?.Engine?.TrackerManagerInternal;
        if (trackerManager == null || !trackerManager.IsRunning) return;

        await trackerManager.ForceReannounceAsync().ConfigureAwait(false);
    }

    public async Task<bool> ChangeLocationAsync(string infoHash, string newSavePath)
    {
        return await _orchestrator.ChangeSavePathAsync(infoHash, newSavePath).ConfigureAwait(false);
    }

    public void ApplyTorrentSettings(string infoHash, TorrentSettings settings)
    {
        _orchestrator.ApplyTorrentSettings(infoHash, settings);
    }

    public async Task SetFilePrioritiesAsync(string infoHash, IList<(int fileIndex, FilePriority priority)> priorities)
    {
        await Task.Run(() =>
        {
            var managed = _orchestrator.TorrentsInternal.Find(infoHash);
            if (managed == null) return;

            var priorityArray = new FilePriority[managed.Torrent?.Info.Files.Count ?? 0];
            for (int i = 0; i < priorityArray.Length; i++)
                priorityArray[i] = FilePriority.Normal;

            foreach (var (fileIndex, priority) in priorities)
            {
                if (fileIndex >= 0 && fileIndex < priorityArray.Length)
                    priorityArray[fileIndex] = priority;
            }

            managed.Engine?.DownloadCoordinatorInternal?.SetFilePriorities(priorityArray);
        }).ConfigureAwait(false);
    }

    public async Task<(int Added, int Removed)> UpdateTorrentTrackers(string infoHash, IList<string> trackerUrls)
    {
        var managed = _orchestrator.TorrentsInternal.Find(infoHash);
        var trackerManager = managed?.Engine?.TrackerManagerInternal;
        if (trackerManager == null) return (0, 0);

        var newSet = new HashSet<string>(trackerUrls, StringComparer.OrdinalIgnoreCase);
        var currentUrls = trackerManager.GetAllTrackerStatistics().Keys;
        var currentSet = new HashSet<string>(currentUrls, StringComparer.OrdinalIgnoreCase);

        int added = 0, removed = 0;
        var addedUrls = new List<string>();
        var removedUrls = new List<string>();

        foreach (var url in newSet.Except(currentSet))
        {
            if (trackerManager.AddTracker(url))
            {
                added++;
                addedUrls.Add(url);
            }
        }
        foreach (var url in currentSet.Except(newSet))
        {
            if (trackerManager.RemoveTracker(url))
            {
                removed++;
                removedUrls.Add(url);
            }
        }

        // Persist to database
        var persistence = _orchestrator.Persistence;
        foreach (var url in addedUrls)
            await persistence.AddTrackersAsync(infoHash, new[] { (url, 0) }).ConfigureAwait(false);
        foreach (var url in removedUrls)
            await persistence.RemoveTrackerAsync(infoHash, url).ConfigureAwait(false);

        // Force immediate announce so new trackers discover peers now
        if (added > 0 && trackerManager.IsRunning)
            await trackerManager.ForceReannounceAsync().ConfigureAwait(false);

        return (added, removed);
    }

    public async Task<(int Added, int Removed)> UpdateTorrentWebSeeds(string infoHash, IList<string> webSeedUrls)
    {
        var managed = _orchestrator.TorrentsInternal.Find(infoHash);
        var webSeedManager = managed?.Engine?.WebSeedManagerInternal;
        if (webSeedManager == null) return (0, 0);

        var newSet = new HashSet<string>(webSeedUrls, StringComparer.OrdinalIgnoreCase);
        var currentUrls = new HashSet<string>(
            webSeedManager.AllSeeds.Select(s => s.Url), StringComparer.OrdinalIgnoreCase);

        int added = 0, removed = 0;
        var addedUrls = new List<string>();
        var removedUrls = new List<string>();

        foreach (var url in newSet.Except(currentUrls))
        {
            if (webSeedManager.AddSeed(url, Download.WebSeedType.BEP19))
            {
                added++;
                addedUrls.Add(url);
            }
        }
        foreach (var url in currentUrls.Except(newSet))
        {
            if (webSeedManager.RemoveSeed(url))
            {
                removed++;
                removedUrls.Add(url);
            }
        }

        // Persist to database
        var persistence = _orchestrator.Persistence;
        foreach (var url in addedUrls)
            await persistence.AddWebSeedAsync(infoHash, url, "BEP19").ConfigureAwait(false);
        foreach (var url in removedUrls)
            await persistence.RemoveWebSeedAsync(infoHash, url).ConfigureAwait(false);

        return (added, removed);
    }

    private async Task ApplyAddOptionsAsync(string infoHash, TorrentAddOptions options)
    {
        await Task.Run(() =>
        {
            var managed = _orchestrator.TorrentsInternal.Find(infoHash);
            if (managed == null) return;

            if (options.SequentialDownload)
                managed.SequentialDownload = true;

            if (options.FirstLastPiecePriority)
                managed.FirstLastPiecePriority = true;

            if (options.FilePriorities != null)
            {
                if (managed.Engine?.FileProgressTrackerInternal != null)
                    managed.Engine.SetAllFilePriorities(options.FilePriorities);
                else
                    managed.PendingFilePriorities = options.FilePriorities;

                managed.ResumeData.FilePriorities ??= new Dictionary<int, int>();
                managed.ResumeData.FilePriorities.Clear();
                for (int i = 0; i < options.FilePriorities.Length; i++)
                {
                    if (options.FilePriorities[i] != FilePriority.Normal)
                        managed.ResumeData.FilePriorities[i] = (int)options.FilePriorities[i];
                }
            }

            // Seed mode: mark all pieces as "have" with lazy verification (libtorrent parity)
            if (options.SeedMode && managed.ResumeData.PieceCount > 0)
            {
                int pieceCount = managed.ResumeData.PieceCount;
                managed.ResumeData.Flags |= vTorrent.Core.ResumeData.TorrentFlags.SeedMode;
                var allOnes = new System.Collections.BitArray(pieceCount, true);
                managed.ResumeData.SetHavePieces(allOnes);
                managed.ResumeData.VerifiedPieces = new byte[(pieceCount + 7) / 8];
            }
        }).ConfigureAwait(false);
    }

    #endregion

    #region Queries

    public IReadOnlyList<TorrentSnapshot> GetTorrents()
    {
        return _orchestrator.TorrentsInternal.ToList()
            .Select(m => m.CreateSnapshot())
            .ToList();
    }

    public TorrentSnapshot? GetTorrent(string infoHash)
    {
        var managed = _orchestrator.TorrentsInternal.Find(infoHash);
        return managed?.CreateSnapshot();
    }

    public ManagedTorrentView? GetTorrentDetails(string infoHash)
    {
        if (!IsInitialized || string.IsNullOrEmpty(infoHash)) return null;
        return _orchestrator.GetTorrentView(infoHash);
    }

    public int GetDownloadingCount()
    {
        // Intent gate: paused torrents keep Phase=Downloading (orthogonal state model)
        return _orchestrator.TorrentsInternal.ToList()
            .Count(m => m.GetStatus() is { Phase: TransferPhase.Downloading, Intent: UserIntent.Active });
    }

    public int GetSeedingCount()
    {
        return _orchestrator.TorrentsInternal.ToList()
            .Count(m => m.GetStatus() is { Phase: TransferPhase.Seeding, Intent: UserIntent.Active });
    }

    public int GetPausedCount()
    {
        return _orchestrator.TorrentsInternal.ToList()
            .Count(m => m.GetStatus().Intent == UserIntent.Paused);
    }

    public int GetCompletedCount()
    {
        return _orchestrator.TorrentsInternal.ToList()
            .Count(m => m.IsFinished);
    }

    #endregion

    #region Queue

    public void SetQueuePositionTop(string infoHash) => _orchestrator.SetQueuePositionTop(infoHash);
    public void SetQueuePositionBottom(string infoHash) => _orchestrator.SetQueuePositionBottom(infoHash);
    public void SetQueuePositionUp(string infoHash) => _orchestrator.SetQueuePositionUp(infoHash);
    public void SetQueuePositionDown(string infoHash) => _orchestrator.SetQueuePositionDown(infoHash);

    #endregion

    #region Categories

    public async Task<IReadOnlyList<Category>> GetAllCategoriesAsync()
        => await _orchestrator.Persistence.GetAllCategoriesAsync();

    public async Task<Category?> GetCategoryAsync(int id)
        => await _orchestrator.Persistence.GetCategoryAsync(id);

    public async Task<Category> CreateCategoryAsync(string name, string? color = null, string? savePath = null)
    {
        var category = await _orchestrator.Persistence.CreateCategoryAsync(name, color, savePath);
        CategoryChanged?.Invoke(this, category.Id);
        return category;
    }

    public async Task UpdateCategoryAsync(int id, string name, string? color, string? savePath)
    {
        await _orchestrator.Persistence.UpdateCategoryAsync(id, name, color, savePath);
        CategoryChanged?.Invoke(this, id);
    }

    public async Task DeleteCategoryAsync(int id)
    {
        await _orchestrator.Persistence.DeleteCategoryAsync(id);
        CategoryChanged?.Invoke(this, id);
    }

    public async Task<int> GetTorrentCountByCategoryAsync(int categoryId)
        => await _orchestrator.Persistence.GetTorrentCountByCategoryAsync(categoryId);

    public async Task SetTorrentCategoryAsync(string infoHash, int? categoryId)
        => await _orchestrator.Persistence.SetTorrentCategoryAsync(infoHash, categoryId);

    #endregion

    #region Tags

    public async Task<IReadOnlyList<Tag>> GetAllTagsAsync()
        => await _orchestrator.Persistence.GetAllTagsAsync();

    public async Task<Tag?> GetTagAsync(int id)
        => await _orchestrator.Persistence.GetTagAsync(id);

    public async Task<Tag> CreateTagAsync(string name, string? color = null)
    {
        var tag = await _orchestrator.Persistence.CreateTagAsync(name, color);
        TagChanged?.Invoke(this, tag.Id);
        return tag;
    }

    public async Task UpdateTagAsync(int id, string name, string? color)
    {
        await _orchestrator.Persistence.UpdateTagAsync(id, name, color);
        TagChanged?.Invoke(this, id);
    }

    public async Task DeleteTagAsync(int id)
    {
        await _orchestrator.Persistence.DeleteTagAsync(id);
        TagChanged?.Invoke(this, id);
    }

    public async Task<int> GetTorrentCountByTagAsync(int tagId)
        => await _orchestrator.Persistence.GetTorrentCountByTagAsync(tagId);

    public async Task<IReadOnlyList<Tag>> GetTorrentTagsAsync(string infoHash)
        => await _orchestrator.Persistence.GetTorrentTagsAsync(infoHash);

    public async Task AddTorrentTagAsync(string infoHash, int tagId)
        => await _orchestrator.Persistence.AddTorrentTagAsync(infoHash, tagId);

    public async Task RemoveTorrentTagAsync(string infoHash, int tagId)
        => await _orchestrator.Persistence.RemoveTorrentTagAsync(infoHash, tagId);

    public async Task SetTorrentTagsAsync(string infoHash, IEnumerable<int> tagIds)
        => await _orchestrator.Persistence.SetTorrentTagsAsync(infoHash, tagIds);

    #endregion

    #region Notifications

    public void NotifyProfileChanged(string profileName)
    {
        ProfileChanged?.Invoke(this, profileName);
    }

    public void NotifyScheduleToggled(bool enabled)
    {
        ScheduleToggled?.Invoke(this, enabled);
    }

    #endregion

    #region DHT

    public bool IsDhtRunning => _orchestrator.IsDhtRunning;
    public bool IsDhtEnabled => _orchestrator.IsDhtEnabled;
    public int DhtNodeCount => _orchestrator.DhtNodeCount;

    public async Task ToggleDhtAsync()
    {
        await _orchestrator.ToggleDhtAsync();
    }

    #endregion

    #region Settings

    public async Task ApplySettingsAsync()
    {
        var settingsManager = _orchestrator.Persistence.SettingsManager;
        if (settingsManager?.Current == null) return;
        _orchestrator.ApplySettings(settingsManager.Current);
        await Task.CompletedTask;
    }

    #endregion

    #region Events

    public event EventHandler<string>? TorrentAdded;
    public event EventHandler<string>? TorrentRemoved;
    public event EventHandler<string>? TorrentCompleted;
    public event EventHandler<SessionStatistics>? StatsUpdated;
    public event EventHandler<Abstractions.Events.TorrentStatusChangedEventArgs>? TorrentStatusChanged;
    public event EventHandler<Abstractions.Events.TorrentErrorEventArgs>? TorrentError;
    public event EventHandler<Abstractions.Events.DhtStateChangedEventArgs>? DhtStateChanged;
    public event EventHandler<int>? CategoryChanged;
    public event EventHandler<int>? TagChanged;
    public event EventHandler<string>? ProfileChanged;
    public event EventHandler<bool>? ScheduleToggled;

    private void OnOrchestratorTorrentAdded(object? sender, TorrentAddedEventArgs e)
    {
        TorrentAdded?.Invoke(this, e.InfoHash);
    }

    private void OnOrchestratorTorrentRemoved(object? sender, TorrentRemovedEventArgs e)
    {
        TorrentRemoved?.Invoke(this, e.InfoHash);
    }

    private void OnOrchestratorTorrentCompleted(object? sender, TorrentCompletedEventArgs e)
    {
        TorrentCompleted?.Invoke(this, e.InfoHash);
    }

    private void OnOrchestratorStatisticsUpdated(object? sender, StatisticsUpdatedEventArgs e)
    {
        StatsUpdated?.Invoke(this, e.Statistics);
    }

    private void OnOrchestratorTorrentStatusChanged(object? sender, Core.Events.TorrentStatusChangedEventArgs e)
        => TorrentStatusChanged?.Invoke(this, new Abstractions.Events.TorrentStatusChangedEventArgs(e.InfoHash, e.Name, e.OldStatus, e.NewStatus));

    private void OnOrchestratorTorrentFailed(object? sender, Core.Events.TorrentFailedEventArgs e)
        => TorrentError?.Invoke(this, new Abstractions.Events.TorrentErrorEventArgs(e.InfoHash, e.Error));

    private void OnOrchestratorDhtStateChanged(object? sender, Core.Events.DhtStateChangedEventArgs e)
        => DhtStateChanged?.Invoke(this, new Abstractions.Events.DhtStateChangedEventArgs(e.IsRunning, e.NodeCount));

    #endregion

    #region Extended Queries

    public bool[]? GetPieceStates(string infoHash)
    {
        var managed = _orchestrator.TorrentsInternal.Find(infoHash);
        if (managed?.Engine == null) return null;

        var bitfield = managed.Engine.LocalBitfieldInternal;
        if (bitfield == null) return null;

        var states = new bool[bitfield.PieceCount];
        for (int i = 0; i < bitfield.PieceCount; i++)
            states[i] = bitfield.HasPiece(i);

        return states;
    }

    public IReadOnlyList<PeerStatsView>? GetPeerStats(string infoHash)
    {
        var managed = _orchestrator.TorrentsInternal.Find(infoHash);
        if (managed?.Engine == null) return null;

        var statistics = managed.Engine.TorrentStatisticsInternal;
        if (statistics == null) return null;

        var allPeerStats = statistics.GetAllPeerStats();
        var result = new List<PeerStatsView>();

        foreach (var (peer, stats) in allPeerStats)
        {
            // PeerBitfield is byte[] (raw bitfield), compute progress manually
            float progress = 0f;
            if (peer.PeerBitfield != null && managed.Engine!.PieceCount > 0)
            {
                int havePieces = 0;
                int totalPieces = managed.Engine.PieceCount;
                for (int i = 0; i < totalPieces; i++)
                {
                    int byteIndex = i / 8;
                    int bitIndex = 7 - (i % 8); // MSB-first per BitTorrent protocol
                    if (byteIndex < peer.PeerBitfield.Length && (peer.PeerBitfield[byteIndex] & (1 << bitIndex)) != 0)
                        havePieces++;
                }
                progress = (float)havePieces / totalPieces;
            }

            // Build peer flags string: D=downloading, U=uploading, S=snubbed,
            // I=interested, E=encrypted, K=choking, H=incoming, T=uTP, X=seed
            var flags = new System.Text.StringBuilder(10);
            if (!peer.IsChoked && peer.IsInterested) flags.Append('D');
            if (!peer.IsChoking && peer.PeerIsInterested) flags.Append('U');
            if (peer.IsSnubbed) flags.Append('S');
            if (peer.IsInterested) flags.Append('I');
            if (peer.IsEncrypted) flags.Append('E');
            if (peer.IsIncoming) flags.Append('H');
            if (peer.IsUtp) flags.Append('T');
            if (peer.IsSeed) flags.Append('X');

            result.Add(new PeerStatsView
            {
                Endpoint = peer.PeerInfo?.EndPoint?.ToString() ?? "",
                Client = peer.ClientName,
                PayloadDownloaded = stats.PayloadDownloaded,
                PayloadUploaded = stats.PayloadUploaded,
                PayloadDownloadRate = (int)stats.PayloadDownloadRate,
                PayloadUploadRate = (int)stats.PayloadUploadRate,
                Progress = progress,
                Flags = flags.ToString()
            });
        }

        return result;
    }

    #endregion
}
