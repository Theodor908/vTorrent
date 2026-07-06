using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Models;
using vTorrent.Desktop.Controls;
using vTorrent.Core;
using vTorrent.Core.Persistence;
using vTorrent.Desktop.ViewModels;
using vTorrent.Desktop.ViewModels.Settings;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private ITorrentManagerService? _torrentManager;
    private IDialogService? _dialogService;
    private IServiceProvider? _serviceProvider;
    private SessionPersistence? _persistence;
    private PersistedWindowState? _savedWindowState;
    private bool _isClosing;
    private bool _isReallyQuitting;

    /// <summary>
    /// Call this to allow the window to actually close (for app quit).
    /// </summary>
    public void RequestClose()
    {
        _isReallyQuitting = true;
        Close();
    }

    public MainWindow()
    {
        InitializeComponent();

        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);

        // Window state persistence
        this.Closing += OnWindowClosing;
        this.PropertyChanged += OnWindowPropertyChanged;

        DataContextChanged += OnDataContextChanged;

        // Enable drag-drop for torrent files
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Load taskbar/alt-tab icon from assets
        try
        {
            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Images", "logo256x256.ico");
            if (System.IO.File.Exists(iconPath))
            {
                Icon = new WindowIcon(iconPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load window icon: {ex.Message}");
        }
    }

    /// <summary>
    /// Show an in-app toast notification
    /// </summary>
    public void ShowToast(string title, string message, ToastType type = ToastType.Info)
    {
        ToastNotification?.Show(title, message, type);
    }

    public MainWindow(ITorrentManagerService? torrentManager) : this()
    {
        _torrentManager = torrentManager;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from old view model
        if (_viewModel?.Sidebar != null)
        {
            _viewModel.Sidebar.AddFromFileRequested -= OnAddFromFileRequested;
            _viewModel.Sidebar.AddFromMagnetRequested -= OnAddFromMagnetRequested;
            _viewModel.Sidebar.OpenSettingsRequested -= OnOpenSettingsRequested;
            _viewModel.Sidebar.OpenToolsWindowRequested -= OnOpenToolsWindowRequested;
            _viewModel.Sidebar.EditCategoryRequested -= OnEditCategoryRequested;
            _viewModel.Sidebar.EditTagRequested -= OnEditTagRequested;
            _viewModel.Sidebar.CreateCategoryRequested -= OnCreateCategoryRequested;
            _viewModel.Sidebar.CreateTagRequested -= OnCreateTagRequested;
        }

        if (_viewModel?.TorrentList != null)
        {
            _viewModel.TorrentList.EditTorrentRequested -= OnEditTorrentRequested;
        }

        // Subscribe to new view model
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel?.Sidebar != null)
        {
            _viewModel.Sidebar.AddFromFileRequested += OnAddFromFileRequested;
            _viewModel.Sidebar.AddFromMagnetRequested += OnAddFromMagnetRequested;
            _viewModel.Sidebar.OpenSettingsRequested += OnOpenSettingsRequested;
            _viewModel.Sidebar.OpenToolsWindowRequested += OnOpenToolsWindowRequested;
            _viewModel.Sidebar.EditCategoryRequested += OnEditCategoryRequested;
            _viewModel.Sidebar.EditTagRequested += OnEditTagRequested;
            _viewModel.Sidebar.CreateCategoryRequested += OnCreateCategoryRequested;
            _viewModel.Sidebar.CreateTagRequested += OnCreateTagRequested;
        }

        if (_viewModel?.TorrentList != null)
        {
            _viewModel.TorrentList.EditTorrentRequested += OnEditTorrentRequested;
        }
    }

    private async void OnAddFromFileRequested(object? sender, EventArgs e)
    {
        try
        {
            // Open file picker for torrent files
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Torrent File",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Torrent Files")
                    {
                        Patterns = new[] { "*.torrent" },
                        MimeTypes = new[] { "application/x-bittorrent" }
                    },
                    FilePickerFileTypes.All
                }
            });

            if (files.Count == 0)
                return;

            var file = files[0];
            var filePath = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(filePath))
                return;

            await AddTorrentFromFileAsync(filePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnAddFromFileRequested: {ex.Message}");
        }
    }

    /// <summary>
    /// Add a torrent from file path, checking for duplicates and showing the add dialog
    /// </summary>
    private async Task AddTorrentFromFileAsync(string filePath)
    {
        // Check if this torrent already exists before showing the dialog
        var infoHash = await GetTorrentInfoHashAsync(filePath);
        if (!string.IsNullOrEmpty(infoHash))
        {
            var existingTorrent = _torrentManager?.GetTorrentViewModel(infoHash);
            if (existingTorrent != null)
            {
                // Torrent already exists - show in-app notification
                ShowToast(
                    "Torrent Already Exists",
                    $"\"{existingTorrent.Name}\" is already in your list.",
                    ToastType.Warning);
                return;
            }
        }

        // Create and show the Add Torrent dialog
        if (_dialogService == null) return;
        var result = await _dialogService.ShowAddTorrentDialogAsync(this, filePath);

        if (result)
        {
            // Torrent was added successfully - refresh the sidebar categories/tags
            if (_viewModel?.Sidebar != null)
            {
                await _viewModel.Sidebar.RefreshCategoriesAndTagsAsync();
            }
        }
    }

    /// <summary>
    /// Get the info hash from a torrent file without fully loading it
    /// </summary>
    private async Task<string?> GetTorrentInfoHashAsync(string filePath)
    {
        try
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(filePath);
            var parser = new Bencode.Parsers.BencodeParser();
            var parsed = parser.Parse(bytes, out _);

            if (parsed is Bencode.Objects.BDictionary dict)
            {
                var torrent = Bencode.Torrents.Torrent.FromBDictionary(dict);
                return torrent.GetInfoHashHex();
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }

    private async void OnAddFromMagnetRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_dialogService == null) return;

            var result = await _dialogService.ShowAddMagnetDialogAsync(this);

            if (result && _viewModel?.Sidebar != null)
            {
                await _viewModel.Sidebar.RefreshCategoriesAndTagsAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnAddFromMagnetRequested: {ex.Message}");
        }
    }

    private async void OnOpenSettingsRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_dialogService == null) return;

            await _dialogService.ShowSettingsDialogAsync(this);

            // Re-check profile drift after settings dialog closes
            if (_viewModel != null)
            {
                await _viewModel.LoadProfileStateAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnOpenSettingsRequested: {ex.Message}");
        }
    }

    private async void OnOpenToolsWindowRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_dialogService == null) return;
            await _dialogService.ShowToolsWindowAsync(this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnOpenToolsWindowRequested: {ex.Message}");
        }
    }

    private async void OnEditTorrentRequested(object? sender, string infoHash)
    {
        try
        {
            if (_dialogService == null) return;
            await _dialogService.ShowToolsWindowAsync(this, preselectedInfoHash: infoHash, initialTab: 1);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnEditTorrentRequested: {ex.Message}");
        }
    }

    private async void OnEditCategoryRequested(object? sender, SidebarMenuItemViewModel item)
    {
        try
        {
            if (item.DatabaseId == null || _dialogService == null) return;

            var result = await _dialogService.ShowEditCategoryDialogAsync(
                this,
                item.DatabaseId.Value,
                item.Name,
                item.SavePath,
                item.TagColor);

            if (result == null) return;

            if (result.IsDeleted)
            {
                if (_viewModel?.Sidebar != null)
                    await _viewModel.Sidebar.DeleteCategoryAsync(item.DatabaseId.Value);
            }
            else
            {
                if (_viewModel?.Sidebar != null)
                {
                    await _viewModel.Sidebar.UpdateCategoryAsync(
                        item.DatabaseId.Value,
                        result.Name,
                        result.SavePath,
                        result.Color);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnEditCategoryRequested: {ex.Message}");
        }
    }

    private async void OnEditTagRequested(object? sender, SidebarMenuItemViewModel item)
    {
        try
        {
            if (item.DatabaseId == null || _dialogService == null) return;

            var result = await _dialogService.ShowEditTagDialogAsync(
                this,
                item.DatabaseId.Value,
                item.Name,
                item.TagColor);

            if (result == null) return;

            if (result.IsDeleted)
            {
                if (_viewModel?.Sidebar != null)
                    await _viewModel.Sidebar.DeleteTagAsync(item.DatabaseId.Value);
            }
            else
            {
                if (_viewModel?.Sidebar != null)
                {
                    await _viewModel.Sidebar.UpdateTagAsync(
                        item.DatabaseId.Value,
                        result.Name,
                        result.Color);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnEditTagRequested: {ex.Message}");
        }
    }

    private async void OnCreateCategoryRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_dialogService == null) return;

            var result = await _dialogService.ShowCreateCategoryDialogAsync(this);

            if (result == null) return;

            if (_viewModel?.Sidebar != null)
            {
                await _viewModel.Sidebar.CreateCategoryFromDialogAsync(
                    result.Name,
                    result.SavePath,
                    result.Color);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnCreateCategoryRequested: {ex.Message}");
        }
    }

    private async void OnCreateTagRequested(object? sender, EventArgs e)
    {
        try
        {
            if (_dialogService == null) return;

            var result = await _dialogService.ShowCreateTagDialogAsync(this);

            if (result == null) return;

            if (_viewModel?.Sidebar != null)
            {
                await _viewModel.Sidebar.CreateTagFromDialogAsync(
                    result.Name,
                    result.Color);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnCreateTagRequested: {ex.Message}");
        }
    }

    /// <summary>
    /// Set the torrent manager service for the window
    /// </summary>
    public void SetTorrentManager(ITorrentManagerService? torrentManager)
    {
        _torrentManager = torrentManager;
        _dialogService = new DialogService(torrentManager, _serviceProvider);

        if (_torrentManager != null)
        {
            _torrentManager.InAppNotificationReceived += (sender, args) =>
            {
                var toastType = args.Type switch
                {
                    NotificationType.Success => ToastType.Success,
                    NotificationType.Warning => ToastType.Warning,
                    NotificationType.Error => ToastType.Error,
                    _ => ToastType.Info
                };
                ShowToast(args.Title, args.Message, toastType);
            };
        }
    }

    /// <summary>
    /// Set the persistence layer for window state
    /// </summary>
    public void SetPersistence(SessionPersistence? persistence)
    {
        _persistence = persistence;
    }

    public void SetServiceProvider(IServiceProvider? serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Restore window state from persistence
    /// </summary>
    public async Task RestoreWindowStateAsync()
    {
        if (_persistence == null)
            return;

        try
        {
            _savedWindowState = await _persistence.LoadWindowStateAsync();

            // Apply window state
            if (_savedWindowState.IsMaximized)
            {
                WindowState = Avalonia.Controls.WindowState.Maximized;
            }
            else
            {
                // Validate and apply position
                if (_savedWindowState.HasValidSize())
                {
                    Width = _savedWindowState.Width;
                    Height = _savedWindowState.Height;
                }

                if (_savedWindowState.HasValidPosition())
                {
                    Position = new PixelPoint(_savedWindowState.X, _savedWindowState.Y);
                }
            }
        }
        catch
        {
            // Ignore errors - just use default window state
        }
    }

    /// <summary>
    /// Save current window state to persistence
    /// </summary>
    public async Task SaveWindowStateAsync()
    {
        if (_persistence == null)
            return;

        try
        {
            var state = new PersistedWindowState
            {
                IsMaximized = WindowState == Avalonia.Controls.WindowState.Maximized,
                X = Position.X,
                Y = Position.Y,
                Width = (int)Width,
                Height = (int)Height
            };

            await _persistence.SaveWindowStateAsync(state);
        }
        catch
        {
            // Ignore errors during save
        }
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        try
        {
            if (_isReallyQuitting)
            {
                // Allow the close to proceed — app is quitting
                if (!_isClosing)
                {
                    _isClosing = true;
                    await SaveWindowStateAsync();
                }
                return;
            }

            var closeToTray = _persistence?.Settings.UI.CloseToTray ?? true;
            if (closeToTray)
            {
                // Hide to tray instead of closing
                e.Cancel = true;
                this.Hide();
            }
            else
            {
                // Actually quit the app — same path as tray menu "Quit"
                _isReallyQuitting = true;
                e.Cancel = false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnWindowClosing: {ex.Message}");
        }
    }

    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty) return;
        if (e.NewValue is not WindowState newState) return;
        if (newState != WindowState.Minimized) return;

        var minimizeToTray = _persistence?.Settings.UI.MinimizeToTray ?? false;
        if (minimizeToTray)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                WindowState = WindowState.Normal;
                this.Hide();
            });
        }
    }

    #region Drag and Drop

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Check if the dragged data contains files
        if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles();
            if (files != null)
            {
                // Check if any file is a .torrent file
                var hasTorrentFile = files.Any(f =>
                {
                    var path = f.TryGetLocalPath();
                    return !string.IsNullOrEmpty(path) &&
                           path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
                });

                if (hasTorrentFile)
                {
                    e.DragEffects = DragDropEffects.Copy;
                    e.Handled = true;
                    return;
                }
            }
        }

        e.DragEffects = DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.Contains(DataFormats.Files))
                return;

            var files = e.Data.GetFiles();
            if (files == null)
                return;

            // Get all .torrent files from the drop
            var torrentFiles = files
                .Select(f => f.TryGetLocalPath())
                .Where(path => !string.IsNullOrEmpty(path) &&
                              path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (torrentFiles.Count == 0)
                return;

            e.Handled = true;

            // Add the first torrent file (or could loop through all)
            foreach (var filePath in torrentFiles)
            {
                if (!string.IsNullOrEmpty(filePath))
                {
                    await AddTorrentFromFileAsync(filePath);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnDrop: {ex.Message}");
        }
    }

    #endregion

    #region Command Line / Startup Item Processing

    /// <summary>
    /// Process a startup item (torrent file or magnet link) passed via command line.
    /// </summary>
    /// <param name="item">The startup item to process.</param>
    public async Task ProcessStartupItemAsync(StartupItem item)
    {
        if (!item.IsValid)
        {
            ShowToast(
                "Invalid Startup Item",
                item.ErrorMessage ?? "Unknown error",
                ToastType.Error);
            return;
        }

        switch (item.Type)
        {
            case StartupItemType.TorrentFile:
                await AddTorrentFromFileAsync(item.Value);
                break;

            case StartupItemType.MagnetUri:
                await AddMagnetFromUriAsync(item.Value);
                break;

            default:
                ShowToast(
                    "Unknown Item Type",
                    $"Cannot process: {item.Value}",
                    ToastType.Warning);
                break;
        }
    }

    /// <summary>
    /// Add a magnet link from URI, checking for duplicates and showing the add dialog.
    /// </summary>
    private async Task AddMagnetFromUriAsync(string magnetUri)
    {
        // Try to extract info hash to check for duplicates
        var infoHash = ExtractInfoHashFromMagnet(magnetUri);
        if (!string.IsNullOrEmpty(infoHash))
        {
            var existingTorrent = _torrentManager?.GetTorrentViewModel(infoHash);
            if (existingTorrent != null)
            {
                // Torrent already exists - show in-app notification
                ShowToast(
                    "Torrent Already Exists",
                    $"\"{existingTorrent.Name}\" is already in your list.",
                    ToastType.Warning);
                return;
            }
        }

        // Create and show the Add Magnet Link dialog with the URI pre-filled
        if (_dialogService == null) return;
        var result = await _dialogService.ShowAddMagnetDialogAsync(this, magnetUri);

        if (result)
        {
            // Magnet link was added successfully - refresh the sidebar categories/tags
            if (_viewModel?.Sidebar != null)
            {
                await _viewModel.Sidebar.RefreshCategoriesAndTagsAsync();
            }
        }
    }

    /// <summary>
    /// Extract info hash from a magnet URI for duplicate detection.
    /// </summary>
    private static string? ExtractInfoHashFromMagnet(string magnetUri)
    {
        if (string.IsNullOrEmpty(magnetUri))
            return null;

        // Look for btih: (BitTorrent info hash)
        var btihIndex = magnetUri.IndexOf("btih:", StringComparison.OrdinalIgnoreCase);
        if (btihIndex < 0)
            return null;

        var start = btihIndex + 5;
        var end = magnetUri.IndexOf('&', start);
        var hash = end >= 0
            ? magnetUri.Substring(start, end - start)
            : magnetUri.Substring(start);

        // Clean up the hash (remove any trailing characters)
        hash = hash.Trim();

        // Validate hash length (SHA1 = 40 hex chars, Base32 = 32 chars)
        if (hash.Length == 40 || hash.Length == 32)
            return hash.ToUpperInvariant();

        return null;
    }

    #endregion

    #region Tray Menu Public API

    /// <summary>
    /// Opens the Add Torrent file picker + dialog (called from tray menu).
    /// </summary>
    public async void OpenAddTorrentDialog()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Torrent File",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Torrent Files")
                    {
                        Patterns = new[] { "*.torrent" },
                        MimeTypes = new[] { "application/x-bittorrent" }
                    },
                    FilePickerFileTypes.All
                }
            });

            if (files.Count == 0)
                return;

            var filePath = files[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(filePath))
                await AddTorrentFromFileAsync(filePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error opening add torrent dialog from tray: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the Add Magnet Link dialog (called from tray menu).
    /// </summary>
    public async void OpenAddMagnetDialog()
    {
        try
        {
            if (_dialogService == null) return;

            var result = await _dialogService.ShowAddMagnetDialogAsync(this);

            if (result && _viewModel?.Sidebar != null)
                await _viewModel.Sidebar.RefreshCategoriesAndTagsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error opening magnet dialog from tray: {ex.Message}");
        }
    }

    #endregion
}
