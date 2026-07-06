using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Storage;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels;

/// <summary>
/// ViewModel for the sidebar navigation.
/// Handles navigation between sections and maintains menu state.
/// Follows Single Responsibility - only sidebar/navigation concerns.
/// </summary>
public partial class SidebarViewModel : BaseViewModel
{
    private readonly INavigationService? _navigationService;
    private readonly TorrentListViewModel? _torrentListViewModel;
    private readonly IThemeService? _themeService;
    private readonly ITorrentManagerService? _torrentManager;
    private readonly CategoryService? _categoryService;
    private readonly TagService? _tagService;

    [ObservableProperty]
    private ObservableCollection<SidebarMenuItemViewModel> _overviewItems = new();

    [ObservableProperty]
    private ObservableCollection<SidebarMenuItemViewModel> _categoriesItems = new();

    [ObservableProperty]
    private ObservableCollection<SidebarMenuItemViewModel> _allCategoriesItems = new();

    [ObservableProperty]
    private ObservableCollection<SidebarMenuItemViewModel> _tagsItems = new();

    [ObservableProperty]
    private ObservableCollection<SidebarMenuItemViewModel> _allTagsItems = new();

    [ObservableProperty]
    private ObservableCollection<SidebarMenuItemViewModel> _settingsItems = new();

    [ObservableProperty]
    private SidebarMenuItemViewModel? _selectedItem;

    [ObservableProperty]
    private int _unreadNotificationCount;

    [ObservableProperty]
    private bool _notificationsEnabled;

    // Flag to prevent re-saving settings when updating from external source
    private bool _isUpdatingNotificationsFromExternal;

    partial void OnNotificationsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(NotificationIcon));

        // Only persist if this change was from user interaction, not from external update
        if (!_isUpdatingNotificationsFromExternal && _torrentManager?.SettingsManager != null)
        {
            _torrentManager.SettingsManager.Current.UI.NotificationsEnabled = value;
            _ = _torrentManager.SettingsManager.SaveAsync();
        }
    }

    /// <summary>
    /// Returns the appropriate notification icon based on toggle state.
    /// E5E8 = bell (on), E0D4 = bell-slash (off)
    /// </summary>
    public string NotificationIcon => NotificationsEnabled ? "\uE5E8" : "\uE0D4";

    [ObservableProperty]
    private bool _darkThemeEnabled = true;

    // Flag to prevent re-triggering theme service when updating from external source
    private bool _isUpdatingThemeFromExternal;

    partial void OnDarkThemeEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ThemeLabel));
        OnPropertyChanged(nameof(ThemeIcon));

        // Only change theme if this was from user interaction, not from external update
        if (!_isUpdatingThemeFromExternal)
        {
            _themeService?.SetTheme(value ? ThemeMode.Dark : ThemeMode.Light);

            // Persist to settings (ThemeService handles the actual save, but we sync UI settings too)
            if (_torrentManager?.SettingsManager != null)
            {
                _torrentManager.SettingsManager.Current.UI.Theme = value ? "Dark" : "Light";
                // Note: ThemeService.SetTheme already saves, so we don't need to save again here
            }
        }
    }

    /// <summary>
    /// Returns the appropriate theme label based on toggle state.
    /// </summary>
    public string ThemeLabel => DarkThemeEnabled ? "Dark" : "Light";

    /// <summary>
    /// Returns the appropriate theme icon based on toggle state.
    /// E3C6 = moon (dark), E532 = sun (light)
    /// </summary>
    public string ThemeIcon => DarkThemeEnabled ? "\uE58E" : "\uE472";

    [ObservableProperty]
    private bool _isSearchExpanded = false;

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        // Propagate search to torrent list
        if (_torrentListViewModel != null)
        {
            _torrentListViewModel.SearchQuery = value;
        }
    }

    [ObservableProperty]
    private bool _isCategoriesPanelOpen = false;

    [ObservableProperty]
    private bool _isTagsPanelOpen = false;

    [ObservableProperty]
    private bool _isDhtRunning;

    [ObservableProperty]
    private bool _isDhtInitializing;

    /// <summary>
    /// Returns the appropriate DHT icon based on state.
    /// E288 = globe (connected/initializing), E286 = globe-simple (not connected)
    /// </summary>
    public string DhtIcon => (IsDhtRunning || IsDhtInitializing) ? "\uE288" : "\uE286";

    [ObservableProperty]
    private int _dhtNodeCount;

    /// <summary>
    /// Tooltip for DHT status
    /// </summary>
    public string DhtTooltip => IsDhtInitializing ? "DHT: Connecting..." :
                                 IsDhtRunning ? $"DHT: Connected ({DhtNodeCount} nodes)" :
                                 "DHT: Disabled (click to enable)";

    partial void OnDhtNodeCountChanged(int value)
    {
        OnPropertyChanged(nameof(DhtTooltip));
    }

    partial void OnIsDhtRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(DhtIcon));
        OnPropertyChanged(nameof(DhtTooltip));
    }

    partial void OnIsDhtInitializingChanged(bool value)
    {
        OnPropertyChanged(nameof(DhtIcon));
        OnPropertyChanged(nameof(DhtTooltip));
        ToggleDhtCommand.NotifyCanExecuteChanged();
    }

    private const int MaxVisibleItems = 4;

    /// <summary>
    /// Event raised when a torrent file should be added
    /// </summary>
    public event EventHandler? AddFromFileRequested;

    /// <summary>
    /// Event raised when a magnet link should be added
    /// </summary>
    public event EventHandler? AddFromMagnetRequested;

    /// <summary>
    /// Event raised when the settings window should be opened
    /// </summary>
    public event EventHandler? OpenSettingsRequested;

    /// <summary>
    /// Event raised when the tools window should be opened
    /// </summary>
    public event EventHandler? OpenToolsWindowRequested;

    /// <summary>
    /// Event raised when a category should be edited (double-click)
    /// </summary>
    public event EventHandler<SidebarMenuItemViewModel>? EditCategoryRequested;

    /// <summary>
    /// Event raised when a tag should be edited (double-click)
    /// </summary>
    public event EventHandler<SidebarMenuItemViewModel>? EditTagRequested;

    /// <summary>
    /// Event raised when a new category should be created via dialog
    /// </summary>
    public event EventHandler? CreateCategoryRequested;

    /// <summary>
    /// Event raised when a new tag should be created via dialog
    /// </summary>
    public event EventHandler? CreateTagRequested;

    /// <summary>
    /// Design-time constructor
    /// </summary>
    public SidebarViewModel() : this(null, null, null, null)
    {
    }

    /// <summary>
    /// Runtime constructor with dependency injection
    /// </summary>
    public SidebarViewModel(
        INavigationService? navigationService,
        TorrentListViewModel? torrentListViewModel,
        IThemeService? themeService = null,
        ITorrentManagerService? torrentManager = null)
    {
        _navigationService = navigationService;
        _torrentListViewModel = torrentListViewModel;
        _themeService = themeService;
        _torrentManager = torrentManager;

        // Create decomposed services
        if (torrentManager != null)
        {
            _categoryService = new CategoryService(torrentManager);
            _tagService = new TagService(torrentManager);

            // Subscribe to core events so category/tag list updates live from any source
            _categoryService.SubscribeToCoreEvents();
            _categoryService.CategoriesUpdated += OnCategoriesUpdated;

            _tagService.SubscribeToCoreEvents();
            _tagService.TagsUpdated += OnTagsUpdated;
        }

        // Load toggle states from settings
        if (_torrentManager?.SettingsManager != null)
        {
            var uiSettings = _torrentManager.SettingsManager.Current.UI;
            _notificationsEnabled = uiSettings.NotificationsEnabled;
            _darkThemeEnabled = uiSettings.Theme != "Light"; // Dark if not explicitly Light
        }
        else if (_themeService != null)
        {
            // Fallback: sync toggle state with theme service
            _darkThemeEnabled = _themeService.IsDarkTheme;
        }

        InitializeMenuItems();

        // Subscribe to torrent list changes to update counts
        if (_torrentListViewModel != null)
        {
            _torrentListViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(TorrentListViewModel.TotalCount) or
                    nameof(TorrentListViewModel.CategoryTotalCount) or
                    nameof(TorrentListViewModel.DownloadingCount) or
                    nameof(TorrentListViewModel.SeedingCount) or
                    nameof(TorrentListViewModel.CompletedCount) or
                    nameof(TorrentListViewModel.ErroredCount))
                {
                    UpdateCounts();
                }
            };
        }

        // Wire up DHT state changes
        if (_torrentManager != null)
        {
            // Initial state
            IsDhtRunning = _torrentManager.Service.IsDhtRunning;
            IsDhtInitializing = _torrentManager.IsDhtInitializing;
            DhtNodeCount = _torrentManager.Service.DhtNodeCount;

            // Subscribe to changes
            _torrentManager.DhtStateChanged += OnDhtStateChanged;

            // Subscribe to notification settings changes
            if (_torrentManager.NotificationService != null)
            {
                _torrentManager.NotificationService.SettingsChanged += OnNotificationSettingsChanged;

                if (_torrentManager.NotificationService is NotificationService ns)
                {
                    ns.InAppNotificationRequested += (_, _) =>
                    {
                        UnreadNotificationCount++;
                    };
                }
            }
        }

        // Subscribe to theme changes
        if (_themeService != null)
        {
            _themeService.ThemeChanged += OnThemeChanged;
        }
    }

    private void OnDhtStateChanged(object? sender, DesktopDhtStateChangedEventArgs e)
    {
        IsDhtRunning = e.IsRunning;
        IsDhtInitializing = e.IsInitializing;
        DhtNodeCount = e.NodeCount;
    }

    private void OnThemeChanged(object? sender, ThemeMode mode)
    {
        // Update sidebar toggle to match new theme (without triggering the partial method's save logic)
        var isDark = mode != ThemeMode.Light;
        if (DarkThemeEnabled != isDark)
        {
            // Set flag to prevent re-triggering theme service in the partial method
            _isUpdatingThemeFromExternal = true;
            try
            {
                DarkThemeEnabled = isDark;
            }
            finally
            {
                _isUpdatingThemeFromExternal = false;
            }
        }
    }

    private void OnNotificationSettingsChanged(object? sender, bool isEnabled)
    {
        // Update sidebar toggle to match new settings (without triggering the partial method's save logic)
        if (NotificationsEnabled != isEnabled)
        {
            // Set flag to prevent re-saving settings in the partial method
            _isUpdatingNotificationsFromExternal = true;
            try
            {
                NotificationsEnabled = isEnabled;
            }
            finally
            {
                _isUpdatingNotificationsFromExternal = false;
            }
        }
    }

    private void OnCategoriesUpdated()
    {
        // Event may fire on a background thread — marshal to UI thread before touching collections
        Dispatcher.UIThread.Post(async () =>
        {
            if (_categoryService == null) return;
            try
            {
                var categories = await _categoryService.LoadAllWithCountsAsync();
                AllCategoriesItems.Clear();

                // Keep the "All" placeholder as the first entry
                AllCategoriesItems.Add(new SidebarMenuItemViewModel
                {
                    Name = "All",
                    ItemType = SidebarItemType.Category,
                    DatabaseId = null,
                    IsSelected = true
                });

                foreach (var cat in categories)
                {
                    AllCategoriesItems.Add(new SidebarMenuItemViewModel
                    {
                        Name = cat.Name,
                        TagColor = cat.Color,
                        SavePath = cat.SavePath,
                        Count = cat.TorrentCount,
                        ItemType = SidebarItemType.Category,
                        DatabaseId = cat.Id
                    });
                }
                UpdateVisibleCategories();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to refresh categories: {ex.Message}");
            }
        });
    }

    private void OnTagsUpdated()
    {
        // Event may fire on a background thread — marshal to UI thread before touching collections
        Dispatcher.UIThread.Post(async () =>
        {
            if (_tagService == null) return;
            try
            {
                var tags = await _tagService.LoadAllWithCountsAsync();
                AllTagsItems.Clear();
                foreach (var tag in tags)
                {
                    AllTagsItems.Add(new SidebarMenuItemViewModel
                    {
                        Name = tag.Name,
                        TagColor = tag.Color,
                        Count = tag.TorrentCount,
                        ItemType = SidebarItemType.Tag,
                        DatabaseId = tag.Id
                    });
                }
                UpdateVisibleTags();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to refresh tags: {ex.Message}");
            }
        });
    }

    private void InitializeMenuItems()
    {
        // Overview section items - using Unicode escape sequences
        OverviewItems = new ObservableCollection<SidebarMenuItemViewModel>
        {
            new SidebarMenuItemViewModel
            {
                Name = "All",
                Icon = "\uE8F4", // Grid icon
                Count = _torrentListViewModel?.CategoryTotalCount ?? 0,
                IsSelected = true,
                ItemType = SidebarItemType.Overview
            },
            new SidebarMenuItemViewModel
            {
                Name = "Downloading",
                Icon = "\uE03E", // Arrow down icon
                Count = _torrentListViewModel?.DownloadingCount ?? 0,
                ItemType = SidebarItemType.Downloading
            },
            new SidebarMenuItemViewModel
            {
                Name = "Seeding",
                Icon = "\uE08E", // Arrow up icon
                Count = _torrentListViewModel?.SeedingCount ?? 0,
                ItemType = SidebarItemType.Seeding
            },
            new SidebarMenuItemViewModel
            {
                Name = "Errored",
                Icon = "\uE4F6", // X icon
                Count = _torrentListViewModel?.ErroredCount ?? 0,
                ItemType = SidebarItemType.Errored
            },
            new SidebarMenuItemViewModel
            {
                Name = "Completed",
                Icon = "\uECF2", // Flag icon
                Count = _torrentListViewModel?.CompletedCount ?? 0,
                ItemType = SidebarItemType.Completed
            }
        };

        // Set the first item as selected
        SelectedItem = OverviewItems[0];

        // Categories and tags will be loaded from the database
        AllCategoriesItems = new ObservableCollection<SidebarMenuItemViewModel>();
        CategoriesItems = new ObservableCollection<SidebarMenuItemViewModel>();
        AllTagsItems = new ObservableCollection<SidebarMenuItemViewModel>();
        TagsItems = new ObservableCollection<SidebarMenuItemViewModel>();

        // Settings section items
        SettingsItems = new ObservableCollection<SidebarMenuItemViewModel>
        {
            new SidebarMenuItemViewModel
            {
                Name = "Settings",
                Icon = "\uE270", // Sliders icon
                ItemType = SidebarItemType.Settings
            }
        };

        // Load categories and tags from database
        _ = LoadCategoriesAndTagsAsync();
    }

    /// <summary>
    /// Loads categories and tags from the database
    /// </summary>
    private async Task LoadCategoriesAndTagsAsync()
    {
        if (_categoryService == null || _tagService == null) return;

        try
        {
            // Load categories via service
            var categories = await _categoryService.LoadAllWithCountsAsync();
            AllCategoriesItems.Clear();

            // Add "All" category as the first item (null DatabaseId means all categories)
            AllCategoriesItems.Add(new SidebarMenuItemViewModel
            {
                Name = "All",
                ItemType = SidebarItemType.Category,
                DatabaseId = null,
                IsSelected = true  // Default selection
            });

            foreach (var cat in categories)
            {
                AllCategoriesItems.Add(new SidebarMenuItemViewModel
                {
                    Name = cat.Name,
                    TagColor = cat.Color,
                    SavePath = cat.SavePath,
                    Count = cat.TorrentCount,
                    ItemType = SidebarItemType.Category,
                    DatabaseId = cat.Id
                });
            }
            UpdateVisibleCategories();

            // Load tags via service
            var tags = await _tagService.LoadAllWithCountsAsync();
            AllTagsItems.Clear();
            foreach (var tag in tags)
            {
                AllTagsItems.Add(new SidebarMenuItemViewModel
                {
                    Name = tag.Name,
                    TagColor = tag.Color,
                    Count = tag.TorrentCount,
                    ItemType = SidebarItemType.Tag,
                    DatabaseId = tag.Id
                });
            }
            UpdateVisibleTags();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load categories/tags: {ex.Message}");
        }
    }

    /// <summary>
    /// Refresh categories and tags from the database
    /// </summary>
    public async Task RefreshCategoriesAndTagsAsync()
    {
        await LoadCategoriesAndTagsAsync();
    }

    private void UpdateCounts()
    {
        if (_torrentListViewModel == null) return;

        if (OverviewItems.Count >= 5)
        {
            // Use CategoryTotalCount for "All" - shows count within selected category
            OverviewItems[0].Count = _torrentListViewModel.CategoryTotalCount;
            OverviewItems[1].Count = _torrentListViewModel.DownloadingCount;
            OverviewItems[2].Count = _torrentListViewModel.SeedingCount;
            OverviewItems[3].Count = _torrentListViewModel.ErroredCount;
            OverviewItems[4].Count = _torrentListViewModel.CompletedCount;
        }
    }

    [RelayCommand]
    private void SelectItem(SidebarMenuItemViewModel? item)
    {
        if (item == null) return;

        // Tags support multi-select - toggle the clicked tag and apply filter
        if (item.ItemType == SidebarItemType.Tag)
        {
            item.IsSelected = !item.IsSelected;

            // Apply tag filter to torrent list
            // Don't navigate - tag selection is orthogonal to overview state filter
            if (item.DatabaseId.HasValue && _torrentListViewModel != null)
            {
                _torrentListViewModel.ToggleTagFilter(item.DatabaseId.Value);
            }

            return;
        }

        // Categories - single selection (including "All" option)
        if (item.ItemType == SidebarItemType.Category)
        {
            // Deselect all categories, then select the clicked one
            foreach (var menuItem in AllCategoriesItems)
                menuItem.IsSelected = false;
            item.IsSelected = true;
            SelectedItem = item;

            // Apply category filter to torrent list
            // DatabaseId is null for "All" category, which means no filter
            // Don't navigate - category selection is orthogonal to overview state filter
            if (_torrentListViewModel != null)
            {
                _torrentListViewModel.CategoryFilter = item.DatabaseId;
            }

            return;
        }

        // Settings opens the settings window
        if (item.ItemType == SidebarItemType.Settings)
        {
            OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // For Overview items, use exclusive selection within overview only
        // Category selection persists - overview filters within the selected category
        foreach (var menuItem in OverviewItems)
            menuItem.IsSelected = false;

        // Select the clicked item
        item.IsSelected = true;
        SelectedItem = item;

        // Navigate using the navigation service
        var section = item.ItemType switch
        {
            SidebarItemType.Overview => NavigationSection.Overview,
            SidebarItemType.Downloading => NavigationSection.Downloading,
            SidebarItemType.Seeding => NavigationSection.Seeding,
            SidebarItemType.Errored => NavigationSection.Errored,
            SidebarItemType.Completed => NavigationSection.Completed,
            _ => NavigationSection.Overview
        };

        _navigationService?.NavigateTo(section);
    }

    [RelayCommand]
    private void AddFromFile()
    {
        AddFromFileRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AddFromMagnet()
    {
        AddFromMagnetRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenToolsWindow()
    {
        OpenToolsWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanToggleDht() => !IsDhtInitializing;

    [RelayCommand(CanExecute = nameof(CanToggleDht))]
    private async Task ToggleDht()
    {
        if (_torrentManager == null) return;

        try
        {
            await _torrentManager.Service.ToggleDhtAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to toggle DHT: {ex.Message}");
        }
    }

    /// <summary>
    /// Add a torrent from a file path. Called by the view after file selection.
    /// </summary>
    public async void AddTorrentFromPath(string torrentPath, string? savePath = null)
    {
        if (_torrentManager == null) return;

        try
        {
            await _torrentManager.AddTorrentAsync(torrentPath, savePath);
        }
        catch (Exception ex)
        {
            // Show error notification to user
            _torrentManager?.NotificationService?.Show(
                "Failed to Add Torrent",
                ex.Message,
                NotificationType.Error);
            System.Diagnostics.Debug.WriteLine($"Failed to add torrent: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchExpanded = !IsSearchExpanded;
        if (!IsSearchExpanded)
        {
            SearchText = string.Empty;
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        IsSearchExpanded = false;
    }

    [RelayCommand]
    private void ToggleExpand(SidebarMenuItemViewModel? item)
    {
        if (item == null || !item.IsExpandable) return;
        item.IsExpanded = !item.IsExpanded;
    }

    [RelayCommand]
    private void ToggleCategoriesPanel()
    {
        if (IsCategoriesPanelOpen)
        {
            IsCategoriesPanelOpen = false;
        }
        else
        {
            IsCategoriesPanelOpen = true;
            IsTagsPanelOpen = false;
        }
    }

    [RelayCommand]
    private void CloseCategoriesPanel()
    {
        IsCategoriesPanelOpen = false;
    }

    [RelayCommand]
    private void ToggleTagsPanel()
    {
        if (IsTagsPanelOpen)
        {
            IsTagsPanelOpen = false;
        }
        else
        {
            IsTagsPanelOpen = true;
            IsCategoriesPanelOpen = false;
        }
    }

    [RelayCommand]
    private void CloseTagsPanel()
    {
        IsTagsPanelOpen = false;
    }

    [RelayCommand]
    private void AddCategory()
    {
        IsCategoriesPanelOpen = true;
        IsTagsPanelOpen = false;
    }

    [RelayCommand]
    private void AddTag()
    {
        IsTagsPanelOpen = true;
        IsCategoriesPanelOpen = false;
    }

    [RelayCommand]
    private void CreateCategory()
    {
        CreateCategoryRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CreateTag()
    {
        CreateTagRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Creates a category from dialog result (called by MainWindow after dialog closes).
    /// </summary>
    public async Task CreateCategoryFromDialogAsync(string name, string? savePath, string? color)
    {
        if (_categoryService == null) return;

        try
        {
            var cat = await _categoryService.CreateAsync(name.Trim());

            // If savePath or color provided, update immediately after creation
            if (!string.IsNullOrWhiteSpace(savePath) || !string.IsNullOrWhiteSpace(color))
            {
                await _categoryService.UpdateAsync(cat.Id, name.Trim(), savePath, color);
            }

            AllCategoriesItems.Add(new SidebarMenuItemViewModel
            {
                Name = cat.Name,
                TagColor = color ?? cat.Color,
                SavePath = savePath ?? cat.SavePath,
                Count = 0,
                ItemType = SidebarItemType.Category,
                DatabaseId = cat.Id
            });
            UpdateVisibleCategories();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create category: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a tag from dialog result (called by MainWindow after dialog closes).
    /// </summary>
    public async Task CreateTagFromDialogAsync(string name, string? color)
    {
        if (_tagService == null) return;

        try
        {
            var tag = await _tagService.CreateAsync(name.Trim());

            if (!string.IsNullOrWhiteSpace(color))
            {
                await _tagService.UpdateAsync(tag.Id, name.Trim(), color);
            }

            AllTagsItems.Add(new SidebarMenuItemViewModel
            {
                Name = tag.Name,
                TagColor = color ?? tag.Color,
                Count = 0,
                ItemType = SidebarItemType.Tag,
                DatabaseId = tag.Id
            });
            UpdateVisibleTags();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create tag: {ex.Message}");
        }
    }

    /// <summary>
    /// Called when a category is double-clicked for editing
    /// </summary>
    [RelayCommand]
    private void EditCategory(SidebarMenuItemViewModel? item)
    {
        // Don't allow editing the "All" category
        if (item == null || item.DatabaseId == null) return;
        if (item.ItemType != SidebarItemType.Category) return;

        EditCategoryRequested?.Invoke(this, item);
    }

    /// <summary>
    /// Called when a tag is double-clicked for editing
    /// </summary>
    [RelayCommand]
    private void EditTag(SidebarMenuItemViewModel? item)
    {
        if (item == null || item.DatabaseId == null) return;
        if (item.ItemType != SidebarItemType.Tag) return;

        EditTagRequested?.Invoke(this, item);
    }

    /// <summary>
    /// Update a category after editing
    /// </summary>
    public async Task UpdateCategoryAsync(int categoryId, string name, string? savePath, string? color)
    {
        if (_categoryService == null) return;

        await _categoryService.UpdateAsync(categoryId, name, savePath, color);

        // Update UI
        var item = AllCategoriesItems.FirstOrDefault(c => c.DatabaseId == categoryId);
        if (item != null)
        {
            item.Name = name;
            item.SavePath = savePath;
            item.TagColor = color;
        }
    }

    /// <summary>
    /// Delete a category
    /// </summary>
    public async Task DeleteCategoryAsync(int categoryId)
    {
        if (_categoryService == null) return;

        await _categoryService.DeleteAsync(categoryId);

        // Update UI
        var item = AllCategoriesItems.FirstOrDefault(c => c.DatabaseId == categoryId);
        if (item != null)
        {
            // Check if the deleted category was selected
            bool wasSelected = item.IsSelected;

            AllCategoriesItems.Remove(item);
            UpdateVisibleCategories();

            // If the deleted category was selected, reset to "All" category
            if (wasSelected)
            {
                var allCategory = AllCategoriesItems.FirstOrDefault(c => c.DatabaseId == null);
                if (allCategory != null)
                {
                    allCategory.IsSelected = true;
                    SelectedItem = allCategory;

                    // Clear category filter in torrent list
                    if (_torrentListViewModel != null)
                    {
                        _torrentListViewModel.CategoryFilter = null;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Update a tag after editing
    /// </summary>
    public async Task UpdateTagAsync(int tagId, string name, string? color)
    {
        if (_tagService == null) return;

        await _tagService.UpdateAsync(tagId, name, color);

        // Update UI
        var item = AllTagsItems.FirstOrDefault(t => t.DatabaseId == tagId);
        if (item != null)
        {
            item.Name = name;
            item.TagColor = color;
        }
    }

    /// <summary>
    /// Delete a tag
    /// </summary>
    public async Task DeleteTagAsync(int tagId)
    {
        if (_tagService == null) return;

        await _tagService.DeleteAsync(tagId);

        // Clear active tag filter for the deleted tag before removing from UI,
        // otherwise the filter predicate references a non-existent tag and
        // no torrents can match, causing the grid to appear empty.
        if (_torrentListViewModel != null && _torrentListViewModel.IsTagFilterActive(tagId))
        {
            _torrentListViewModel.ToggleTagFilter(tagId);
        }

        // Update UI
        var item = AllTagsItems.FirstOrDefault(t => t.DatabaseId == tagId);
        if (item != null)
        {
            AllTagsItems.Remove(item);
            UpdateVisibleTags();
        }
    }

    private void UpdateVisibleCategories()
    {
        CategoriesItems.Clear();
        var visible = AllCategoriesItems.Count > MaxVisibleItems
            ? AllCategoriesItems.Take(MaxVisibleItems)
            : AllCategoriesItems;

        foreach (var item in visible)
        {
            CategoriesItems.Add(item);
        }

        OnPropertyChanged(nameof(HasMoreCategories));
    }

    private void UpdateVisibleTags()
    {
        TagsItems.Clear();
        var visible = AllTagsItems.Count > MaxVisibleItems
            ? AllTagsItems.Take(MaxVisibleItems)
            : AllTagsItems;

        foreach (var item in visible)
        {
            TagsItems.Add(item);
        }

        OnPropertyChanged(nameof(HasMoreTags));
    }

    public bool HasMoreCategories => AllCategoriesItems.Count > MaxVisibleItems;
    public bool HasMoreTags => AllTagsItems.Count > MaxVisibleItems;
}

/// <summary>
/// ViewModel for individual sidebar menu items.
/// </summary>
public partial class SidebarMenuItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _icon = string.Empty;

    [ObservableProperty]
    private int? _count;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpandable;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private SidebarItemType _itemType;

    [ObservableProperty]
    private ObservableCollection<SidebarMenuItemViewModel>? _children;

    [ObservableProperty]
    private string? _tagColor;

    /// <summary>
    /// Database ID for categories and tags
    /// </summary>
    [ObservableProperty]
    private int? _databaseId;

    /// <summary>
    /// Save path for categories
    /// </summary>
    [ObservableProperty]
    private string? _savePath;

    public bool HasCount => Count.HasValue && Count.Value > 0;

    public bool HasTagColor => !string.IsNullOrEmpty(TagColor);

    partial void OnCountChanged(int? value)
    {
        OnPropertyChanged(nameof(HasCount));
    }

    partial void OnTagColorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasTagColor));
    }

    public bool HasDatabaseId => DatabaseId.HasValue;

    partial void OnDatabaseIdChanged(int? value)
    {
        OnPropertyChanged(nameof(HasDatabaseId));
    }
}

/// <summary>
/// Types of sidebar items for categorization
/// </summary>
public enum SidebarItemType
{
    Overview,
    Downloading,
    Seeding,
    Completed,
    Errored,
    Category,
    Tag,
    Settings
}
