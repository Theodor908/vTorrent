using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Settings;
using vTorrent.Storage;
using vTorrent.Desktop.ViewModels;
using vTorrent.Desktop.ViewModels.Settings;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the Torrent Options dialog.
/// Allows editing per-torrent settings like save path, speed limits, and seeding limits.
/// Supports multiple torrent selection - changes apply to all selected torrents.
/// </summary>
public partial class TorrentOptionsViewModel : ObservableObject
{
    private readonly ITorrentManagerService? _torrentManager;
    private readonly SettingsManager? _settingsManager;
    private TorrentSettings? _torrentSettings;
    private string _infoHash = string.Empty;
    private string _originalSavePath = string.Empty;

    // Multi-torrent support
    private List<TorrentViewModel> _torrents = new();
    private List<string> _infoHashes = new();

    #region Torrent Info

    [ObservableProperty]
    private string _torrentName = string.Empty;

    /// <summary>
    /// Whether multiple torrents are being edited
    /// </summary>
    public bool IsMultipleTorrents => _torrents.Count > 1;

    /// <summary>
    /// Number of torrents being edited
    /// </summary>
    public int TorrentCount => _torrents.Count;

    #endregion

    #region Save Location

    [ObservableProperty]
    private string _savePath = string.Empty;

    [ObservableProperty]
    private bool _autoManaged = true;

    #endregion

    #region Category

    [ObservableProperty]
    private ObservableCollection<CategoryItemViewModel> _categories = new();

    [ObservableProperty]
    private CategoryItemViewModel? _selectedCategory;

    partial void OnSelectedCategoryChanged(CategoryItemViewModel? value)
    {
        // Update save path based on category selection
        if (value?.Id == null)
        {
            // "None" selected - use default save path
            var defaultPath = _settingsManager?.Current.Disk.DefaultSavePath;
            if (!string.IsNullOrEmpty(defaultPath))
            {
                SavePath = defaultPath;
            }
        }
        else if (!string.IsNullOrEmpty(value.SavePath))
        {
            // Category has a custom save path - use it
            SavePath = value.SavePath;
        }
    }

    private int? _originalCategoryId;

    #endregion

    #region Tags

    /// <summary>
    /// All available tags in the system
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<TagItemViewModel> _allTags = new();

    /// <summary>
    /// Tags currently assigned to this torrent
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableTags))]
    [NotifyPropertyChangedFor(nameof(HasTags))]
    private ObservableCollection<TagItemViewModel> _torrentTags = new();

    /// <summary>
    /// Tags available to add (not yet assigned)
    /// </summary>
    public IEnumerable<TagItemViewModel> AvailableTags =>
        AllTags.Where(t => !TorrentTags.Any(tt => tt.Id == t.Id));

    /// <summary>
    /// Whether the torrent has any tags
    /// </summary>
    public bool HasTags => TorrentTags.Count > 0;

    /// <summary>
    /// Whether any tags exist in the system at all
    /// </summary>
    public bool HasAnySystemTags => AllTags.Count > 0;

    /// <summary>
    /// Selected tag to add from dropdown
    /// </summary>
    [ObservableProperty]
    private TagItemViewModel? _selectedTagToAdd;

    /// <summary>
    /// Original tag IDs for change detection
    /// </summary>
    private HashSet<int> _originalTagIds = new();

    #endregion

    #region Speed Limits

    [ObservableProperty]
    private ObservableCollection<string> _speedUnitOptions = new() { "KB/s", "MB/s", "GB/s" };

    [ObservableProperty]
    private string _speedUnit = "KB/s";

    partial void OnSpeedUnitChanged(string value)
    {
        if (_torrentSettings == null) return;

        var ulBytes = _torrentSettings.UploadLimit > 0 ? _torrentSettings.UploadLimit : 0;
        var dlBytes = _torrentSettings.DownloadLimit > 0 ? _torrentSettings.DownloadLimit : 0;

        UploadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(ulBytes, value);
        DownloadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(dlBytes, value);
    }

    [ObservableProperty]
    private double _uploadLimitDisplay;

    [ObservableProperty]
    private double _downloadLimitDisplay;

    #endregion

    #region Seeding Limits

    [ObservableProperty]
    private ObservableCollection<string> _ratioOptions = new()
    {
        "Default", "0.5", "1.0", "1.5", "2.0", "3.0", "5.0", "10.0", "Unlimited"
    };

    [ObservableProperty]
    private string _selectedRatio = "Default";

    [ObservableProperty]
    private ObservableCollection<string> _seedingTimeOptions = new()
    {
        "Default", "30 min", "1 hour", "2 hours", "6 hours", "12 hours", "1 day", "1 week", "Unlimited"
    };

    [ObservableProperty]
    private string _selectedSeedingTime = "Default";

    [ObservableProperty]
    private ObservableCollection<string> _limitActionOptions = new()
    {
        "Default", "Pause torrent", "Remove torrent"
    };

    [ObservableProperty]
    private string _selectedLimitAction = "Default";

    #endregion

    #region Download Options

    /// <summary>
    /// Sequential download mode - pieces are downloaded in order from first to last.
    /// When enabled, this is applied to both new and running engines via SetSequentialMode().
    /// </summary>
    [ObservableProperty]
    private bool _sequentialDownload;

    /// <summary>
    /// When enabled, the first and last pieces of each file are prioritized.
    /// Useful for media preview / streaming scenarios.
    /// </summary>
    [ObservableProperty]
    private bool _firstLastPiecePriority;

    #endregion

    #region Dialog State

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    #endregion

    #region Events

    public event EventHandler? DialogAccepted;
    public event EventHandler? DialogCancelled;
    public event EventHandler? BrowseFolderRequested;

    #endregion

    #region Constructor

    public TorrentOptionsViewModel()
    {
        // Design-time constructor
        TorrentName = "Sample Torrent";
        SavePath = "C:\\Downloads";
    }

    public TorrentOptionsViewModel(
        ITorrentManagerService? torrentManager,
        SettingsManager? settingsManager)
    {
        _torrentManager = torrentManager;
        _settingsManager = settingsManager;
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Initialize the dialog with the specified torrent
    /// </summary>
    public async Task InitializeAsync(TorrentViewModel torrent)
    {
        await InitializeAsync(new List<TorrentViewModel> { torrent });
    }

    /// <summary>
    /// Initialize the dialog with multiple torrents
    /// </summary>
    public async Task InitializeAsync(IReadOnlyList<TorrentViewModel> torrents)
    {
        if (torrents == null || torrents.Count == 0) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // Store all torrents
            _torrents = torrents.ToList();
            _infoHashes = torrents.Select(t => t.InfoHash).ToList();

            // For backwards compatibility, keep first torrent's info hash
            _infoHash = _infoHashes[0];

            // Set display name based on selection count
            if (torrents.Count == 1)
            {
                TorrentName = torrents[0].Name;
            }
            else
            {
                TorrentName = $"{torrents.Count} torrents selected";
            }

            // Load categories and tags
            await LoadCategoriesAsync();
            await LoadTagsAsync();

            // Load existing torrent settings if available
            if (_settingsManager != null)
            {
                // For single torrent, load its settings
                // For multiple torrents, use defaults (changes will overwrite)
                if (torrents.Count == 1)
                {
                    _torrentSettings = await _settingsManager.GetTorrentSettingsAsync(_infoHash);
                    if (_torrentSettings == null)
                    {
                        _torrentSettings = new TorrentSettings { InfoHash = _infoHash };
                    }
                    LoadSettingsToUI();
                }
                else
                {
                    // Multiple torrents - use default values
                    // Users will set new values that apply to all
                    _torrentSettings = new TorrentSettings { InfoHash = _infoHash };
                    LoadDefaultsToUI();
                }
            }
            else
            {
                // Use default values if no settings manager
                var firstTorrent = torrents[0];
                SavePath = firstTorrent.SavePath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads";
                _originalSavePath = SavePath;
                AutoManaged = true;
                SequentialDownload = false;
                FirstLastPiecePriority = false;
            }

            OnPropertyChanged(nameof(IsMultipleTorrents));
            OnPropertyChanged(nameof(TorrentCount));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load torrent settings: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Load default values for multi-torrent editing
    /// </summary>
    private void LoadDefaultsToUI()
    {
        // Use default save path
        SavePath = _settingsManager?.Current.Disk.DefaultSavePath ?? "";
        _originalSavePath = SavePath;
        AutoManaged = true;
        SequentialDownload = false;
        FirstLastPiecePriority = false;

        // Select "(None)" category by default
        SelectedCategory = Categories.FirstOrDefault();
        _originalCategoryId = null;

        // Clear tags - for multiple torrents, start fresh
        TorrentTags.Clear();
        _originalTagIds.Clear();
        OnPropertyChanged(nameof(AvailableTags));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasAnySystemTags));

        // Default speed limits (0 = use global)
        UploadLimitDisplay = 0;
        DownloadLimitDisplay = 0;

        // Default seeding limits
        SelectedRatio = "Default";
        SelectedSeedingTime = "Default";
        SelectedLimitAction = "Default";
    }

    private async Task LoadCategoriesAsync()
    {
        Categories.Clear();
        Categories.Add(new CategoryItemViewModel { Id = null, Name = "(None)" });

        if (_torrentManager != null)
        {
            try
            {
                var categories = await _torrentManager.Service.GetAllCategoriesAsync();
                foreach (var cat in categories)
                {
                    Categories.Add(new CategoryItemViewModel
                    {
                        Id = cat.Id,
                        Name = cat.Name,
                        Color = cat.Color,
                        SavePath = cat.SavePath
                    });
                }
            }
            catch
            {
                // Categories not available
            }
        }
    }

    private async Task LoadTagsAsync()
    {
        AllTags.Clear();
        TorrentTags.Clear();
        _originalTagIds.Clear();

        if (_torrentManager != null)
        {
            try
            {
                // Load all available tags
                var allTags = await _torrentManager.Service.GetAllTagsAsync();
                foreach (var tag in allTags)
                {
                    AllTags.Add(new TagItemViewModel
                    {
                        Id = tag.Id,
                        Name = tag.Name,
                        Color = tag.Color
                    });
                }

                // Load tags assigned to this torrent
                if (!string.IsNullOrEmpty(_infoHash))
                {
                    var torrentTags = await _torrentManager.Service.GetTorrentTagsAsync(_infoHash);
                    foreach (var tag in torrentTags)
                    {
                        TorrentTags.Add(new TagItemViewModel
                        {
                            Id = tag.Id,
                            Name = tag.Name,
                            Color = tag.Color
                        });
                        _originalTagIds.Add(tag.Id);
                    }
                }

                // Notify that available tags changed
                OnPropertyChanged(nameof(AvailableTags));
                OnPropertyChanged(nameof(HasTags));
            }
            catch
            {
                // Tags not available
            }
        }
    }

    private void LoadSettingsToUI()
    {
        if (_torrentSettings == null) return;

        // Save path - capture original for change detection
        SavePath = _torrentSettings.SavePath ?? _settingsManager?.Current.Disk.DefaultSavePath ?? "";
        _originalSavePath = SavePath;
        AutoManaged = _torrentSettings.AutoManaged;
        SequentialDownload = _torrentSettings.SequentialDownload;
        FirstLastPiecePriority = _torrentSettings.FirstLastPiecePriority;

        // Category - find by name
        SelectedCategory = null;
        if (!string.IsNullOrEmpty(_torrentSettings.Category))
        {
            foreach (var cat in Categories)
            {
                if (cat.Name == _torrentSettings.Category)
                {
                    SelectedCategory = cat;
                    _originalCategoryId = cat.Id;
                    break;
                }
            }
        }

        if (SelectedCategory == null && Categories.Count > 0)
        {
            SelectedCategory = Categories[0]; // (None)
        }

        // Speed limits (convert from bytes to KB)
        var ulBytes = _torrentSettings.UploadLimit > 0 ? _torrentSettings.UploadLimit : 0;
        var dlBytes = _torrentSettings.DownloadLimit > 0 ? _torrentSettings.DownloadLimit : 0;
        SpeedUnit = BandwidthUnitHelper.DetectBestUnit(ulBytes, dlBytes);
        UploadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(ulBytes, SpeedUnit);
        DownloadLimitDisplay = BandwidthUnitHelper.BytesToDisplayUnit(dlBytes, SpeedUnit);

        // Seeding limits
        LoadRatioToUI(_torrentSettings.Seeding.RatioLimit);
        LoadSeedingTimeToUI(_torrentSettings.Seeding.TimeLimitMinutes);
        LoadLimitActionToUI(_torrentSettings.Seeding.PauseWhenComplete, _torrentSettings.Seeding.StopWhenComplete);
    }

    private void LoadRatioToUI(float? ratio)
    {
        if (ratio == null)
        {
            SelectedRatio = "Default";
        }
        else if (ratio == 0)
        {
            SelectedRatio = "Unlimited";
        }
        else
        {
            var ratioStr = ratio.Value.ToString("0.0");
            SelectedRatio = RatioOptions.Contains(ratioStr) ? ratioStr : "Default";
        }
    }

    private void LoadSeedingTimeToUI(int? minutes)
    {
        if (minutes == null)
        {
            SelectedSeedingTime = "Default";
        }
        else if (minutes == 0)
        {
            SelectedSeedingTime = "Unlimited";
        }
        else if (minutes == 30)
        {
            SelectedSeedingTime = "30 min";
        }
        else if (minutes == 60)
        {
            SelectedSeedingTime = "1 hour";
        }
        else if (minutes == 120)
        {
            SelectedSeedingTime = "2 hours";
        }
        else if (minutes == 360)
        {
            SelectedSeedingTime = "6 hours";
        }
        else if (minutes == 720)
        {
            SelectedSeedingTime = "12 hours";
        }
        else if (minutes == 1440)
        {
            SelectedSeedingTime = "1 day";
        }
        else if (minutes == 10080)
        {
            SelectedSeedingTime = "1 week";
        }
        else
        {
            SelectedSeedingTime = "Default";
        }
    }

    private void LoadLimitActionToUI(bool? pause, bool? stop)
    {
        if (pause == true)
        {
            SelectedLimitAction = "Pause torrent";
        }
        else if (stop == true)
        {
            SelectedLimitAction = "Remove torrent";
        }
        else
        {
            SelectedLimitAction = "Default";
        }
    }

    #endregion

    #region Commands

    [RelayCommand]
    private void BrowseFolder()
    {
        BrowseFolderRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AddTag()
    {
        if (SelectedTagToAdd == null) return;

        // Add to torrent tags if not already present
        if (!TorrentTags.Any(t => t.Id == SelectedTagToAdd.Id))
        {
            TorrentTags.Add(new TagItemViewModel
            {
                Id = SelectedTagToAdd.Id,
                Name = SelectedTagToAdd.Name,
                Color = SelectedTagToAdd.Color
            });

            // Notify property changes
            OnPropertyChanged(nameof(AvailableTags));
            OnPropertyChanged(nameof(HasTags));
        }

        // Clear selection
        SelectedTagToAdd = null;
    }

    [RelayCommand]
    private void RemoveTag(TagItemViewModel? tag)
    {
        if (tag == null) return;

        var tagToRemove = TorrentTags.FirstOrDefault(t => t.Id == tag.Id);
        if (tagToRemove != null)
        {
            TorrentTags.Remove(tagToRemove);

            // Notify property changes
            OnPropertyChanged(nameof(AvailableTags));
            OnPropertyChanged(nameof(HasTags));
        }
    }

    [RelayCommand]
    private async Task AcceptAsync()
    {
        if (_settingsManager == null)
        {
            DialogAccepted?.Invoke(this, EventArgs.Empty);
            return;
        }

        ErrorMessage = null;

        try
        {
            // Get current tag IDs for all torrents
            var currentTagIds = TorrentTags.Select(t => t.Id).ToHashSet();

            // Collect torrents that need to be moved (will be done in background)
            var torrentsToMove = new List<(string infoHash, string newPath)>();

            // Apply settings to all selected torrents
            foreach (var infoHash in _infoHashes)
            {
                // Get or create settings for this torrent
                var settings = await _settingsManager.GetTorrentSettingsAsync(infoHash);
                if (settings == null)
                {
                    settings = new TorrentSettings { InfoHash = infoHash };
                }

                // Check if save path changed for this torrent
                var torrent = _torrents.FirstOrDefault(t => t.InfoHash == infoHash);
                var originalPath = torrent?.SavePath ?? "";
                var savePathChanged = !string.Equals(SavePath, originalPath, StringComparison.OrdinalIgnoreCase);

                if (savePathChanged && _torrentManager != null && !string.IsNullOrWhiteSpace(infoHash))
                {
                    // Queue for background move instead of blocking
                    torrentsToMove.Add((infoHash, SavePath));
                }

                // Update settings from UI
                settings.SavePath = SavePath;
                settings.AutoManaged = AutoManaged;
                settings.SequentialDownload = SequentialDownload;
                settings.FirstLastPiecePriority = FirstLastPiecePriority;
                settings.Category = SelectedCategory?.Id != null ? SelectedCategory.Name : null;

                // Speed limits (convert from KB to bytes, -1 = use global)
                var ulBytesOut = BandwidthUnitHelper.DisplayUnitToBytes(UploadLimitDisplay, SpeedUnit);
                var dlBytesOut = BandwidthUnitHelper.DisplayUnitToBytes(DownloadLimitDisplay, SpeedUnit);
                settings.UploadLimit = ulBytesOut > 0 ? ulBytesOut : -1;
                settings.DownloadLimit = dlBytesOut > 0 ? dlBytesOut : -1;

                // Seeding limits
                settings.Seeding.RatioLimit = ParseRatioFromUI();
                settings.Seeding.TimeLimitMinutes = ParseSeedingTimeFromUI();
                ParseLimitActionFromUI(out var pause, out var stop);
                settings.Seeding.PauseWhenComplete = pause;
                settings.Seeding.StopWhenComplete = stop;

                // Save the settings
                await _settingsManager.SaveTorrentSettingsAsync(settings);

                // Apply per-torrent settings to running engine (sequential mode, etc.)
                if (_torrentManager != null)
                {
                    _torrentManager.Service.ApplyTorrentSettings(infoHash, settings);
                }

                // Update tags for this torrent
                // For multiple torrents, always set the new tags (replacing existing)
                // For single torrent, only update if changed
                if (_torrentManager != null)
                {
                    if (IsMultipleTorrents || !currentTagIds.SetEquals(_originalTagIds))
                    {
                        await _torrentManager.Service.SetTorrentTagsAsync(infoHash, currentTagIds);
                    }
                }

                // Category VM update is handled by TorrentManagerService.SetTorrentCategoryAsync
            }

            // Close dialog immediately - settings are saved
            DialogAccepted?.Invoke(this, EventArgs.Empty);

            // Move files in background (fire-and-forget) - dialog is already closed
            if (torrentsToMove.Count > 0 && _torrentManager != null)
            {
                var torrentManager = _torrentManager;
                _ = Task.Run(async () =>
                {
                    foreach (var (infoHash, newPath) in torrentsToMove)
                    {
                        try
                        {
                            await torrentManager.Service.ChangeLocationAsync(infoHash, newPath);
                        }
                        catch
                        {
                            // Log error but don't crash - moves happen in background
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save settings: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogCancelled?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Helpers

    private float? ParseRatioFromUI()
    {
        return SelectedRatio switch
        {
            "Default" => null,
            "Unlimited" => 0f,
            _ => float.TryParse(SelectedRatio, out var ratio) ? ratio : null
        };
    }

    private int? ParseSeedingTimeFromUI()
    {
        return SelectedSeedingTime switch
        {
            "Default" => null,
            "Unlimited" => 0,
            "30 min" => 30,
            "1 hour" => 60,
            "2 hours" => 120,
            "6 hours" => 360,
            "12 hours" => 720,
            "1 day" => 1440,
            "1 week" => 10080,
            _ => null
        };
    }

    private void ParseLimitActionFromUI(out bool? pause, out bool? stop)
    {
        pause = null;
        stop = null;

        switch (SelectedLimitAction)
        {
            case "Pause torrent":
                pause = true;
                break;
            case "Remove torrent":
                stop = true;
                break;
        }
    }

    /// <summary>
    /// Set the save path (called from view after folder selection).
    /// If the user manually selects a path different from the category's save path,
    /// the category is automatically changed to "(None)".
    /// </summary>
    public void SetSavePath(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            SavePath = path;

            // Auto-change category to "(None)" if user manually selects a different path
            // than the current category's save path
            if (SelectedCategory?.Id != null)
            {
                // User has a category selected (not "None")
                var categorySavePath = SelectedCategory.SavePath;
                if (!string.IsNullOrEmpty(categorySavePath) &&
                    !string.Equals(path, categorySavePath, StringComparison.OrdinalIgnoreCase))
                {
                    // User selected a different path than the category's path
                    // Change to "(None)" category
                    var noneCategory = Categories.FirstOrDefault(c => c.Id == null);
                    if (noneCategory != null)
                    {
                        SelectedCategory = noneCategory;
                    }
                }
            }
        }
    }

    #endregion
}
