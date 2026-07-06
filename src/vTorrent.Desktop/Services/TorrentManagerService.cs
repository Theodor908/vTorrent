using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Records;
using vTorrent.Core;
using vTorrent.Core.Events;
using vTorrent.Core.Orchestration;
using vTorrent.Core.Session;
using vTorrent.Core.State;
using CoreTorrentAddedEventArgs = vTorrent.Core.Events.TorrentAddedEventArgs;
using CoreTorrentRemovedEventArgs = vTorrent.Core.Events.TorrentRemovedEventArgs;
using vTorrent.Abstractions.Settings;
using vTorrent.Desktop.ViewModels;

namespace vTorrent.Desktop.Services;

/// <summary>
/// Desktop-specific service that bridges ITorrentService with UI ViewModels.
/// Manages TorrentViewModel lifecycle, UI-thread dispatching, and Desktop-only concerns.
/// Engine operations are available through <see cref="Service"/>; the orchestrator is used only for
/// event subscriptions and ViewModel snapshot creation.
/// </summary>
public class TorrentManagerService : ITorrentManagerService
{
    private readonly ITorrentService _service;
    private readonly TorrentOrchestrator _orchestrator;
    private readonly Dictionary<string, TorrentViewModel> _viewModels = new();
    private readonly Dictionary<string, string?> _displayNameCache = new();
    private readonly object _lock = new();

    private bool _isInitialized;
    private bool _isDisposed;

    public TorrentManagerService(ITorrentService service, TorrentOrchestrator orchestrator)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    }

    #region ITorrentManagerService Properties

    public ITorrentService Service => _service;

    public bool IsInitialized => _isInitialized;

    public IReadOnlyList<TorrentViewModel> Torrents
    {
        get
        {
            lock (_lock)
            {
                return _viewModels.Values.ToList();
            }
        }
    }

    public SessionStatistics SessionStats => _service.SessionStats;

    #endregion

    #region Events

    public event EventHandler<TorrentViewModelEventArgs>? TorrentAdded;
    public event EventHandler<TorrentRemovedEventArgs>? TorrentRemoved;
    public event EventHandler<TorrentViewModelEventArgs>? TorrentUpdated;
    public event EventHandler<StatsUpdatedEventArgs>? StatsUpdated;
    public event EventHandler<TorrentViewModelEventArgs>? TorrentCompleted;
    public event EventHandler<InAppNotificationEventArgs>? InAppNotificationReceived;

    #endregion

    #region Lifecycle

    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        // Initialize orchestrator if needed
        if (!_orchestrator.IsInitialized)
        {
            await _orchestrator.InitializeAsync();
        }

        // Wire up orchestrator events (for ViewModel management)
        _orchestrator.TorrentAdded += OnOrchestratorTorrentAdded;
        _orchestrator.TorrentRemoved += OnOrchestratorTorrentRemoved;
        _orchestrator.TorrentStatusChanged += OnOrchestratorTorrentStatusChanged;
        _orchestrator.TorrentCompleted += OnOrchestratorTorrentCompleted;
        _orchestrator.TorrentFailed += OnOrchestratorTorrentFailed;
        _orchestrator.StatisticsUpdated += OnOrchestratorStatisticsUpdated;
        _orchestrator.TorrentSeedingLimitReached += OnOrchestratorSeedingLimitReached;
        EnsureDhtEventsWired();

        // Create ViewModels for existing torrents
        foreach (var handle in _orchestrator.GetAllTorrents())
        {
            var viewModel = CreateViewModelFromHandle(handle);
            lock (_lock)
            {
                _viewModels[handle.InfoHash] = viewModel;
            }
            _ = RefreshDisplayNameCacheAsync(handle.InfoHash);
        }

        _isInitialized = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        // Unsubscribe from events
        _orchestrator.TorrentAdded -= OnOrchestratorTorrentAdded;
        _orchestrator.TorrentRemoved -= OnOrchestratorTorrentRemoved;
        _orchestrator.TorrentStatusChanged -= OnOrchestratorTorrentStatusChanged;
        _orchestrator.TorrentCompleted -= OnOrchestratorTorrentCompleted;
        _orchestrator.StatisticsUpdated -= OnOrchestratorStatisticsUpdated;
        _orchestrator.TorrentSeedingLimitReached -= OnOrchestratorSeedingLimitReached;

        lock (_lock)
        {
            _viewModels.Clear();
        }

        // Dispose orchestrator
        await _orchestrator.DisposeAsync();
    }

    #endregion

    #region ViewModel Operations (Desktop-specific)

    public async Task<TorrentViewModel> AddTorrentAsync(string torrentPath, string? savePath = null, bool startImmediately = true)
    {
        var infoHash = await _service.AddTorrentAsync(torrentPath, savePath, startImmediately);

        // The orchestrator's TorrentAdded event fires during the await above,
        // which may have already created the ViewModel. Don't overwrite it!
        TorrentViewModel viewModel;
        lock (_lock)
        {
            if (_viewModels.TryGetValue(infoHash, out var existingVm))
            {
                viewModel = existingVm;
            }
            else
            {
                // Create from snapshot if event hasn't fired yet
                var snapshot = _service.GetTorrent(infoHash);
                viewModel = new TorrentViewModel(snapshot ?? new TorrentSnapshot { InfoHash = infoHash, Name = infoHash });
                _viewModels[infoHash] = viewModel;
            }
        }

        return viewModel;
    }

    public async Task<TorrentViewModel> AddTorrentAsync(string torrentPath, TorrentAddOptions options)
    {
        var infoHash = await _service.AddTorrentAsync(torrentPath, options);

        TorrentViewModel viewModel;
        lock (_lock)
        {
            if (_viewModels.TryGetValue(infoHash, out var existingVm))
            {
                viewModel = existingVm;
            }
            else
            {
                var snapshot = _service.GetTorrent(infoHash);
                viewModel = new TorrentViewModel(snapshot ?? new TorrentSnapshot { InfoHash = infoHash, Name = infoHash });
                _viewModels[infoHash] = viewModel;
            }
        }

        return viewModel;
    }

    public async Task<TorrentViewModel> AddMagnetLinkAsync(string magnetUri, string? savePath = null, bool startImmediately = true)
    {
        var infoHash = await _service.AddMagnetAsync(magnetUri, savePath, startImmediately);

        TorrentViewModel viewModel;
        lock (_lock)
        {
            if (_viewModels.TryGetValue(infoHash, out var existingVm))
            {
                viewModel = existingVm;
            }
            else
            {
                var snapshot = _service.GetTorrent(infoHash);
                viewModel = new TorrentViewModel(snapshot ?? new TorrentSnapshot { InfoHash = infoHash, Name = infoHash });
                _viewModels[infoHash] = viewModel;
            }
        }

        return viewModel;
    }

    public async Task<TorrentViewModel> AddMagnetLinkAsync(string magnetUri, TorrentAddOptions options)
    {
        var infoHash = await _service.AddMagnetAsync(magnetUri, options);

        TorrentViewModel viewModel;
        lock (_lock)
        {
            if (_viewModels.TryGetValue(infoHash, out var existingVm))
            {
                viewModel = existingVm;
            }
            else
            {
                var snapshot = _service.GetTorrent(infoHash);
                viewModel = new TorrentViewModel(snapshot ?? new TorrentSnapshot { InfoHash = infoHash, Name = infoHash });
                _viewModels[infoHash] = viewModel;
            }
        }

        return viewModel;
    }

    public TorrentViewModel? GetTorrentViewModel(string infoHash)
    {
        lock (_lock)
        {
            return _viewModels.TryGetValue(infoHash, out var vm) ? vm : null;
        }
    }

    public async Task RefreshDisplayNameAsync(string infoHash)
    {
        await RefreshDisplayNameCacheAsync(infoHash);
    }

    #endregion

    #region Orchestrator Event Handlers

    private void OnOrchestratorTorrentAdded(object? sender, CoreTorrentAddedEventArgs e)
    {
        var handle = _orchestrator.GetTorrent(e.InfoHash);
        if (handle == null)
            return;

        TorrentViewModel viewModel;
        lock (_lock)
        {
            if (_viewModels.ContainsKey(e.InfoHash))
            {
                viewModel = _viewModels[e.InfoHash];
            }
            else
            {
                viewModel = CreateViewModelFromHandle(handle);
                _viewModels[e.InfoHash] = viewModel;
            }
        }

        _ = RefreshDisplayNameCacheAsync(e.InfoHash);

        // Show notification for torrent added
        _notificationService?.NotifyTorrentAdded(viewModel.Name);

        // Raise event on UI thread
        InvokeOnUIThread(() => TorrentAdded?.Invoke(this, new TorrentViewModelEventArgs(viewModel)));
    }

    private void OnOrchestratorTorrentRemoved(object? sender, CoreTorrentRemovedEventArgs e)
    {
        lock (_lock)
        {
            _viewModels.Remove(e.InfoHash);
            _displayNameCache.Remove(e.InfoHash);
        }

        InvokeOnUIThread(() => TorrentRemoved?.Invoke(this, new TorrentRemovedEventArgs(e.InfoHash)));
    }

    private void OnOrchestratorTorrentStatusChanged(object? sender, TorrentStatusChangedEventArgs e)
    {
        TorrentViewModel? viewModel;
        lock (_lock)
        {
            if (!_viewModels.TryGetValue(e.InfoHash, out viewModel))
                return;
        }

        RefreshFromManagedTorrent(viewModel, e.InfoHash);
        InvokeOnUIThread(() => TorrentUpdated?.Invoke(this, new TorrentViewModelEventArgs(viewModel)));
    }

    private void OnOrchestratorTorrentCompleted(object? sender, TorrentCompletedEventArgs e)
    {
        TorrentViewModel? viewModel;
        lock (_lock)
        {
            if (!_viewModels.TryGetValue(e.InfoHash, out viewModel))
                return;
        }

        RefreshFromManagedTorrent(viewModel, e.InfoHash);

        // Show notification
        _notificationService?.NotifyDownloadComplete(viewModel.Name);

        InvokeOnUIThread(() => TorrentCompleted?.Invoke(this, new TorrentViewModelEventArgs(viewModel)));
    }

    private void OnOrchestratorTorrentFailed(object? sender, TorrentFailedEventArgs e)
    {
        TorrentViewModel? viewModel;
        lock (_lock)
        {
            if (!_viewModels.TryGetValue(e.InfoHash, out viewModel))
                return;
        }

        RefreshFromManagedTorrent(viewModel, e.InfoHash);

        // Show notification for download failure
        _notificationService?.NotifyDownloadFailed(viewModel.Name, e.Error);

        InvokeOnUIThread(() => TorrentUpdated?.Invoke(this, new TorrentViewModelEventArgs(viewModel)));
    }

    private void OnOrchestratorStatisticsUpdated(object? sender, StatisticsUpdatedEventArgs e)
    {
        // Get all torrent handles (this is thread-safe, handles wrap ManagedTorrent references)
        var handles = _orchestrator.GetAllTorrents();
        var stats = e.Statistics;

        // All ViewModel updates MUST happen on UI thread to ensure proper binding notifications
        InvokeOnUIThread(() =>
        {
            // Update all torrent ViewModels with latest stats
            foreach (var handle in handles)
            {
                TorrentViewModel? viewModel;
                lock (_lock)
                {
                    if (!_viewModels.TryGetValue(handle.InfoHash, out viewModel))
                    {
                        continue;
                    }
                }

                UpdateViewModelFromHandle(viewModel, handle);
                TorrentUpdated?.Invoke(this, new TorrentViewModelEventArgs(viewModel));
            }

            // Notify session stats updated with torrent list for grid aggregation
            List<TorrentViewModel> torrentList;
            lock (_lock)
            {
                torrentList = _viewModels.Values.ToList();
            }
            StatsUpdated?.Invoke(this, new StatsUpdatedEventArgs(stats, torrentList));
        });
    }

    private void OnOrchestratorSeedingLimitReached(object? sender, SeedingLimitReachedEventArgs e)
    {
        var limitType = e.LimitType.ToString();
        var action = e.Action.ToString();
        _notificationService?.NotifySeedingLimitReached(e.TorrentName, limitType, action);
    }

    #endregion

    #region ViewModel Management

    private async Task RefreshDisplayNameCacheAsync(string infoHash)
    {
        var settings = await (_orchestrator.Persistence.SettingsManager?.GetTorrentSettingsAsync(infoHash)
            ?? Task.FromResult<TorrentSettings?>(null));
        lock (_lock)
        {
            _displayNameCache[infoHash] = settings?.DisplayName;
        }
    }

    private string? GetCachedDisplayName(string infoHash)
    {
        lock (_lock)
        {
            return _displayNameCache.TryGetValue(infoHash, out var name) ? name : null;
        }
    }

    private TorrentSnapshot CreateSnapshotFromHandle(TorrentHandle handle)
    {
        var stats = handle.GetStatistics();
        var managed = _orchestrator.GetManagedTorrent(handle.InfoHash);
        var torrentStatus = managed?.GetStatus() ?? default;
        var totalWanted = handle.TotalWanted > 0 ? handle.TotalWanted : handle.TotalSize;

        return new TorrentSnapshot
        {
            InfoHash = handle.InfoHash,
            Name = handle.Name,
            DisplayName = GetCachedDisplayName(handle.InfoHash),
            Status = torrentStatus,
            TotalSize = handle.TotalSize,
            TotalWanted = totalWanted,
            TotalWantedDone = handle.TotalWantedDone > 0
                ? handle.TotalWantedDone
                : (long)(stats.VerifiedProgress * handle.TotalSize),
            PiecesCompleted = handle.PiecesCompleted,
            TotalPieces = stats.TotalPieces,
            VerifiedProgress = stats.VerifiedProgress,
            PendingPieces = stats.PendingPieces,
            PayloadDownloadRate = handle.PayloadDownloadRate,
            PayloadUploadRate = handle.PayloadUploadRate,
            SmoothedPayloadDownloadRate = handle.SmoothedDownloadRate,
            TotalDownloadRate = (int)stats.DownloadRate,
            TotalUploadRate = (int)stats.UploadRate,
            SessionPayloadDownloaded = stats.AllTimeDownloaded,
            SessionPayloadUploaded = stats.AllTimeUploaded,
            TotalUploaded = handle.AllTimeUploaded,
            ConnectedPeers = handle.ConnectedPeers,
            ConnectedSeeds = handle.ConnectedSeeds,
            TotalPeers = stats.KnownPeers > 0 ? stats.KnownPeers : stats.TrackerLeechers,
            TotalSeeds = stats.TrackerSeeders,
            Availability = stats.Availability,
            IsEndgame = stats.IsEndgame,
            EndgameWastedBytes = stats.EndgameWastedBytes,
            EndgameDuplicateBlocks = stats.EndgameDuplicateBlocks,
            IsSeeding = handle.IsSeed,
            IsFinished = handle.IsFinished,
            AddedOn = handle.AddedTime,
            CompletedOn = handle.CompletedTime,
            ActiveDuration = handle.ActiveDuration,
            SeedingDuration = handle.SeedingDuration,
            SavePath = handle.SavePath,
            QueuePosition = handle.QueuePosition,
            IsForceResumed = !handle.IsAutoManaged,
            CategoryId = handle.CategoryId,
            CategoryName = handle.CategoryName,
            Tags = handle.Tags?.Select(t => t.Name).ToList().AsReadOnly()
                ?? (IReadOnlyList<string>)Array.Empty<string>(),
            ErrorMessage = handle.ErrorMessage,
        };
    }

    private TorrentViewModel CreateViewModelFromHandle(TorrentHandle handle)
    {
        return new TorrentViewModel(CreateSnapshotFromHandle(handle));
    }

    private void UpdateViewModelFromHandle(TorrentViewModel viewModel, TorrentHandle handle)
    {
        viewModel.Update(CreateSnapshotFromHandle(handle));
    }

    private void RefreshFromManagedTorrent(TorrentViewModel viewModel, string infoHash)
    {
        var managed = _orchestrator.GetManagedTorrent(infoHash);
        if (managed != null)
        {
            viewModel.Update(managed.CreateSnapshot());
        }
    }

    private static void InvokeOnUIThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    #endregion

    #region DHT

    public bool IsDhtInitializing => _orchestrator.IsDhtInitializing;

    public event EventHandler<DesktopDhtStateChangedEventArgs>? DhtStateChanged;

    private bool _dhtEventsWired;

    private void EnsureDhtEventsWired()
    {
        if (_dhtEventsWired) return;
        _orchestrator.DhtStateChanged += OnOrchestratorDhtStateChanged;
        _dhtEventsWired = true;
    }

    private void OnOrchestratorDhtStateChanged(object? sender, DhtStateChangedEventArgs e)
    {
        InvokeOnUIThread(() => DhtStateChanged?.Invoke(this, new DesktopDhtStateChangedEventArgs(e.IsRunning, e.IsInitializing, e.NodeCount)));
    }

    public async Task EnableDhtAsync()
    {
        EnsureDhtEventsWired();
        await _orchestrator.EnableDhtAsync();
    }

    public async Task DisableDhtAsync()
    {
        EnsureDhtEventsWired();
        await _orchestrator.DisableDhtAsync();
    }

    #endregion

    #region Settings

    public Core.Settings.SettingsManager? SettingsManager => _orchestrator.Persistence.SettingsManager;

    #endregion

    #region Desktop Services

    private INotificationService? _notificationService;
    private IThemeService? _themeService;

    public INotificationService? NotificationService => _notificationService;
    public IThemeService? ThemeService => _themeService;

    public void SetNotificationService(INotificationService notificationService)
    {
        _notificationService = notificationService;

        _notificationService.InAppNotificationRequested += (sender, args) =>
        {
            InvokeOnUIThread(() => InAppNotificationReceived?.Invoke(this, args));
        };
    }

    public void SetThemeService(IThemeService themeService)
    {
        _themeService = themeService;
    }

    #endregion
}
