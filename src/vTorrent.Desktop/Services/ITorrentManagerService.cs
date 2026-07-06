using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Models;
using vTorrent.Desktop.ViewModels;

namespace vTorrent.Desktop.Services;

/// <summary>
/// Desktop-specific service that bridges ITorrentService with UI ViewModels.
/// Manages TorrentViewModel lifecycle, UI-thread dispatching, and Desktop-only concerns.
/// Engine operations are available through <see cref="Service"/>; this layer adds ViewModel management.
/// </summary>
public interface ITorrentManagerService : IAsyncDisposable
{
    /// <summary>
    /// The underlying platform-agnostic torrent service.
    /// ViewModels should call engine operations directly through this
    /// (e.g. Service.PauseTorrentAsync, Service.GetAllCategoriesAsync, etc.).
    /// </summary>
    ITorrentService Service { get; }

    /// <summary>
    /// Whether the service has been initialized.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Read-only access to all torrent ViewModels.
    /// </summary>
    IReadOnlyList<TorrentViewModel> Torrents { get; }

    /// <summary>
    /// Current session statistics.
    /// </summary>
    SessionStatistics SessionStats { get; }

    #region Desktop Events (ViewModel-typed)

    /// <summary>
    /// Raised when a torrent is added.
    /// </summary>
    event EventHandler<TorrentViewModelEventArgs>? TorrentAdded;

    /// <summary>
    /// Raised when a torrent is removed (info hash is provided).
    /// </summary>
    event EventHandler<TorrentRemovedEventArgs>? TorrentRemoved;

    /// <summary>
    /// Raised when a torrent's data is updated.
    /// </summary>
    event EventHandler<TorrentViewModelEventArgs>? TorrentUpdated;

    /// <summary>
    /// Raised when session statistics are updated.
    /// Includes the torrent list so consumers can aggregate directly from grid.
    /// </summary>
    event EventHandler<StatsUpdatedEventArgs>? StatsUpdated;

    /// <summary>
    /// Raised when a torrent completes downloading.
    /// </summary>
    event EventHandler<TorrentViewModelEventArgs>? TorrentCompleted;

    /// <summary>
    /// Raised when an in-app notification should be shown.
    /// </summary>
    event EventHandler<InAppNotificationEventArgs>? InAppNotificationReceived;

    /// <summary>
    /// Raised when DHT state changes (running, initializing, node count).
    /// </summary>
    event EventHandler<DesktopDhtStateChangedEventArgs>? DhtStateChanged;

    #endregion

    #region Lifecycle

    /// <summary>
    /// Initialize the torrent manager service.
    /// </summary>
    Task InitializeAsync();

    #endregion

    #region ViewModel Operations (Desktop-specific — return TorrentViewModel)

    /// <summary>
    /// Add a torrent from a .torrent file. Returns the created TorrentViewModel.
    /// </summary>
    Task<TorrentViewModel> AddTorrentAsync(string torrentPath, string? savePath = null, bool startImmediately = true);

    /// <summary>
    /// Add a torrent with detailed options. Returns the created TorrentViewModel.
    /// </summary>
    Task<TorrentViewModel> AddTorrentAsync(string torrentPath, TorrentAddOptions options);

    /// <summary>
    /// Add a torrent from a magnet link. Returns the created TorrentViewModel.
    /// </summary>
    Task<TorrentViewModel> AddMagnetLinkAsync(string magnetUri, string? savePath = null, bool startImmediately = true);

    /// <summary>
    /// Add a magnet link with detailed options. Returns the created TorrentViewModel.
    /// </summary>
    Task<TorrentViewModel> AddMagnetLinkAsync(string magnetUri, TorrentAddOptions options);

    /// <summary>
    /// Get a torrent ViewModel by info hash.
    /// </summary>
    TorrentViewModel? GetTorrentViewModel(string infoHash);

    /// <summary>
    /// Refresh the cached display name for a torrent after editing.
    /// </summary>
    Task RefreshDisplayNameAsync(string infoHash);

    #endregion

    #region DHT (Desktop-only — not on ITorrentService)

    /// <summary>
    /// Whether DHT is currently initializing (bootstrapping).
    /// Not available on ITorrentService.
    /// </summary>
    bool IsDhtInitializing { get; }

    /// <summary>
    /// Enable DHT explicitly. Not available on ITorrentService (use Service.ToggleDhtAsync for toggle).
    /// </summary>
    Task EnableDhtAsync();

    /// <summary>
    /// Disable DHT explicitly. Not available on ITorrentService (use Service.ToggleDhtAsync for toggle).
    /// </summary>
    Task DisableDhtAsync();

    #endregion

    #region Settings

    /// <summary>
    /// Get the settings manager for accessing global settings.
    /// </summary>
    Core.Settings.SettingsManager? SettingsManager { get; }

    #endregion

    #region Desktop Services

    INotificationService? NotificationService { get; }
    IThemeService? ThemeService { get; }
    void SetNotificationService(INotificationService notificationService);
    void SetThemeService(IThemeService themeService);

    #endregion
}
