using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Models;
using vTorrent.Core.Persistence;
using vTorrent.Desktop.ViewModels;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels;

/// <summary>
/// ViewModel for the torrent list display.
/// Manages torrent collection, filtering, sorting, and selection.
/// Follows Single Responsibility - only handles list management concerns.
/// </summary>
public partial class TorrentListViewModel : BaseViewModel
{
    private readonly INavigationService? _navigationService;
    private readonly ITorrentManagerService? _torrentManager;
    private SessionPersistence? _persistence;
    private bool _isApplyingViewState;

    #region Observable Properties

    [ObservableProperty]
    private ObservableCollection<TorrentViewModel> _torrents = new();

    [ObservableProperty]
    private ObservableCollection<TorrentViewModel> _filteredTorrents = new();

    [ObservableProperty]
    private TorrentViewModel? _selectedTorrent;

    [ObservableProperty]
    private ObservableCollection<TorrentViewModel> _selectedTorrents = new();

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private TorrentDisplayState? _stateFilter;

    [ObservableProperty]
    private int? _categoryFilter;

    // Tag filters - multiple tags can be selected (acts as additional filter)
    private HashSet<int> _tagFilters = new();

    [ObservableProperty]
    private string _sortColumn = "Name";

    [ObservableProperty]
    private bool _sortAscending = true;

    public List<ColumnDefinition> ColumnDefinitions { get; } = ColumnDefinition.CreateDefaults();

    #endregion

    #region Computed Properties

    public int TotalCount => Torrents.Count;
    public int FilteredCount => FilteredTorrents.Count;

    // Category-filtered counts - these reflect counts within the selected category
    private IEnumerable<TorrentViewModel> CategoryFilteredTorrents =>
        CategoryFilter.HasValue
            ? Torrents.Where(t => t.CategoryId == CategoryFilter.Value)
            : Torrents;

    public int DownloadingCount => CategoryFilteredTorrents.Count(t => t.State is TorrentDisplayState.Downloading or TorrentDisplayState.ForcedDownloading);
    public int SeedingCount => CategoryFilteredTorrents.Count(t => t.State is TorrentDisplayState.Seeding or TorrentDisplayState.ForcedSeeding);
    // Use IsSeeding flag (libtorrent model) for reliable completion detection
    // IsSeeding = all pieces downloaded AND verified (pure seeder)
    public int CompletedCount => CategoryFilteredTorrents.Count(t => t.IsSeeding || t.State is TorrentDisplayState.Seeding or TorrentDisplayState.ForcedSeeding);
    public int PausedCount => CategoryFilteredTorrents.Count(t => t.State == TorrentDisplayState.Paused);
    public int ErroredCount => CategoryFilteredTorrents.Count(t => t.State == TorrentDisplayState.Error);
    // Total within selected category (for "All" in overview)
    public int CategoryTotalCount => CategoryFilteredTorrents.Count();

    #endregion

    #region Services

    /// <summary>
    /// Gets the torrent manager service for dialog access
    /// </summary>
    public ITorrentManagerService? TorrentManager => _torrentManager;

    #endregion

    public TorrentListViewModel() : this(null, null)
    {
    }

    public TorrentListViewModel(INavigationService? navigationService, ITorrentManagerService? torrentManager)
    {
        _navigationService = navigationService;
        _torrentManager = torrentManager;

        // Subscribe to navigation changes for filtering
        if (_navigationService != null)
        {
            _navigationService.NavigationChanged += OnNavigationChanged;
        }

        // Subscribe to torrent manager events if available
        if (_torrentManager != null)
        {
            _torrentManager.TorrentAdded += OnTorrentAdded;
            _torrentManager.TorrentRemoved += OnTorrentRemoved;
            _torrentManager.TorrentUpdated += OnTorrentUpdated;

            // Load initial torrents from service
            LoadTorrentsFromService();
        }
        else
        {
            // Initialize with sample data for design-time or when service is unavailable
            InitializeSampleData();
        }

        ApplyFilter();
    }

    #region Service Integration

    private void LoadTorrentsFromService()
    {
        if (_torrentManager == null)
            return;

        Torrents.Clear();
        foreach (var torrent in _torrentManager.Torrents)
        {
            Torrents.Add(torrent);
        }
        ApplyFilter();
        RefreshCounts();
    }

    private void OnTorrentAdded(object? sender, TorrentViewModelEventArgs e)
    {
        Torrents.Add(e.Torrent);
        ApplyFilter();
        RefreshCounts();
    }

    private void OnTorrentRemoved(object? sender, TorrentRemovedEventArgs e)
    {
        // Torrent may already be removed from the grid (instant removal in RemoveTorrentAsync)
        var torrent = Torrents.FirstOrDefault(t => t.InfoHash == e.InfoHash);
        if (torrent != null)
        {
            Torrents.Remove(torrent);
            FilteredTorrents.Remove(torrent);
            RefreshCounts();
        }
    }

    private void OnTorrentUpdated(object? sender, TorrentViewModelEventArgs e)
    {
        var torrent = e.Torrent;
        // Check if torrent is in FilteredTorrents
        var index = FilteredTorrents.IndexOf(torrent);

        if (index >= 0)
        {
            // Torrent is visible - no need to remove/insert which causes flicker
            // TorrentViewModel properties are [ObservableProperty] so bindings update automatically
            // Just refresh counts in case state changed
        }
        else
        {
            // Torrent not in FilteredTorrents - check if it should be based on current filter
            // This can happen when torrent state changes and it now matches/doesn't match the filter
            var shouldBeVisible = ShouldTorrentBeVisible(torrent);

            if (shouldBeVisible)
            {
                // Re-apply filter to include this torrent
                ApplyFilter();
            }
        }

        RefreshCounts();
    }

    /// <summary>
    /// Check if a torrent should be visible based on current filter settings
    /// </summary>
    private bool ShouldTorrentBeVisible(TorrentViewModel torrent)
    {
        // Check state filter
        if (StateFilter.HasValue && torrent.State != StateFilter.Value)
        {
            return false;
        }

        // Check completed filter - consistent with ApplyFilter()
        // Use IsSeeding flag (libtorrent model) for reliable completion detection
        // IsSeeding = all pieces downloaded AND verified (pure seeder)
        if (_navigationService?.CurrentSection == NavigationSection.Completed)
        {
            if (!torrent.IsSeeding && torrent.State is not (TorrentDisplayState.Seeding or TorrentDisplayState.ForcedSeeding))
            {
                return false;
            }
        }

        // Check search filter
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var query = SearchQuery.ToLowerInvariant();
            if (!torrent.Name.ToLowerInvariant().Contains(query) &&
                !torrent.InfoHash.ToLowerInvariant().Contains(query))
            {
                return false;
            }
        }

        return true;
    }

    #endregion

    #region Navigation Integration

    private void OnNavigationChanged(object? sender, NavigationSection section)
    {
        StateFilter = section switch
        {
            NavigationSection.Downloading => TorrentDisplayState.Downloading,
            NavigationSection.Seeding => TorrentDisplayState.Seeding,
            NavigationSection.Completed => null, // Special handling for completed
            NavigationSection.Errored => TorrentDisplayState.Error,
            _ => null
        };

        ApplyFilter();
    }

    #endregion

    #region Filtering and Sorting

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnStateFilterChanged(TorrentDisplayState? value)
    {
        ApplyFilter();
    }

    partial void OnCategoryFilterChanged(int? value)
    {
        ApplyFilter();
    }

    partial void OnSortColumnChanged(string value)
    {
        ApplySort();
        SaveViewState();
    }

    partial void OnSortAscendingChanged(bool value)
    {
        ApplySort();
        SaveViewState();
    }

    private void ApplyFilter()
    {
        var filtered = Torrents.AsEnumerable();

        // Apply state filter (include forced variants in their base category)
        if (StateFilter.HasValue)
        {
            var sf = StateFilter.Value;
            filtered = sf switch
            {
                TorrentDisplayState.Downloading => filtered.Where(t => t.State is TorrentDisplayState.Downloading or TorrentDisplayState.ForcedDownloading),
                TorrentDisplayState.Seeding => filtered.Where(t => t.State is TorrentDisplayState.Seeding or TorrentDisplayState.ForcedSeeding),
                _ => filtered.Where(t => t.State == sf)
            };
        }

        // FIX: Use IsSeeding flag (libtorrent model) for reliable completion detection
        // "Completed" section shows torrents where IsSeeding = true (all pieces downloaded AND verified)
        // This is more reliable than progress >= 1.0 which can have race conditions with piece verification
        if (_navigationService?.CurrentSection == NavigationSection.Completed)
        {
            filtered = Torrents.Where(t =>
                t.IsSeeding ||
                t.State is TorrentDisplayState.Seeding or TorrentDisplayState.ForcedSeeding);
        }

        // Apply category filter (null = All categories)
        if (CategoryFilter.HasValue)
        {
            filtered = filtered.Where(t => t.CategoryId == CategoryFilter.Value);
        }

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var query = SearchQuery.ToLowerInvariant();
            filtered = filtered.Where(t =>
                t.Name.ToLowerInvariant().Contains(query) ||
                t.InfoHash.ToLowerInvariant().Contains(query));
        }

        // Apply tag filters (torrents must have ANY of the selected tags)
        if (_tagFilters.Count > 0)
        {
            filtered = filtered.Where(t =>
                t.Tags.Any(tag => _tagFilters.Contains(tag.Id)));
        }

        // Apply sorting
        filtered = ApplySortToEnumerable(filtered);

        // Update collection in-place to avoid UI flickering
        UpdateFilteredCollection(filtered.ToList());

        OnPropertyChanged(nameof(FilteredCount));
    }

    private IEnumerable<TorrentViewModel> ApplySortToEnumerable(IEnumerable<TorrentViewModel> source)
    {
        // Always use InfoHash as secondary sort key for stable ordering
        // This prevents items with equal values from jumping around
        return SortColumn switch
        {
            "Name" => SortAscending
                ? source.OrderBy(t => t.EffectiveDisplayName ?? "").ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.EffectiveDisplayName ?? "").ThenBy(t => t.InfoHash),
            "Progress" => SortAscending
                ? source.OrderBy(t => t.Progress).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.Progress).ThenBy(t => t.InfoHash),
            "Size" => SortAscending
                ? source.OrderBy(t => t.TotalSize).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.TotalSize).ThenBy(t => t.InfoHash),
            "TimeLeft" => SortAscending
                ? source.OrderBy(t => t.ETA ?? TimeSpan.MaxValue).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.ETA ?? TimeSpan.MaxValue).ThenBy(t => t.InfoHash),
            "Seeds" => SortAscending
                ? source.OrderBy(t => t.ConnectedSeeds).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.ConnectedSeeds).ThenBy(t => t.InfoHash),
            "Peers" => SortAscending
                ? source.OrderBy(t => t.ConnectedPeers).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.ConnectedPeers).ThenBy(t => t.InfoHash),
            "State" => SortAscending
                ? source.OrderBy(t => t.State).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.State).ThenBy(t => t.InfoHash),
            "DownloadRate" => SortAscending
                ? source.OrderBy(t => t.DownloadRate).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.DownloadRate).ThenBy(t => t.InfoHash),
            "UploadRate" => SortAscending
                ? source.OrderBy(t => t.UploadRate).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.UploadRate).ThenBy(t => t.InfoHash),
            "Ratio" => SortAscending
                ? source.OrderBy(t => t.Ratio).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.Ratio).ThenBy(t => t.InfoHash),
            "TotalDone" => SortAscending
                ? source.OrderBy(t => t.TotalDone).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.TotalDone).ThenBy(t => t.InfoHash),
            "Uploaded" => SortAscending
                ? source.OrderBy(t => t.Uploaded).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.Uploaded).ThenBy(t => t.InfoHash),
            "AddedOn" => SortAscending
                ? source.OrderBy(t => t.AddedOn).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.AddedOn).ThenBy(t => t.InfoHash),
            "CompletedOn" => SortAscending
                ? source.OrderBy(t => t.CompletedOn ?? DateTime.MaxValue).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.CompletedOn ?? DateTime.MaxValue).ThenBy(t => t.InfoHash),
            "Availability" => SortAscending
                ? source.OrderBy(t => t.Availability).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.Availability).ThenBy(t => t.InfoHash),
            "ActiveDuration" => SortAscending
                ? source.OrderBy(t => t.ActiveDuration).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.ActiveDuration).ThenBy(t => t.InfoHash),
            "SeedingDuration" => SortAscending
                ? source.OrderBy(t => t.SeedingDuration).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.SeedingDuration).ThenBy(t => t.InfoHash),
            "SavePath" => SortAscending
                ? source.OrderBy(t => t.SavePath).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.SavePath).ThenBy(t => t.InfoHash),
            "CategoryName" => SortAscending
                ? source.OrderBy(t => t.CategoryName ?? "").ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.CategoryName ?? "").ThenBy(t => t.InfoHash),
            "TagsDisplay" => SortAscending
                ? source.OrderBy(t => t.TagsDisplay).ThenBy(t => t.InfoHash)
                : source.OrderByDescending(t => t.TagsDisplay).ThenBy(t => t.InfoHash),
            _ => source.OrderBy(t => t.Name ?? "").ThenBy(t => t.InfoHash)
        };
    }

    private void UpdateFilteredCollection(List<TorrentViewModel> newItems)
    {
        // Remove items that are no longer in the filtered list
        for (int i = FilteredTorrents.Count - 1; i >= 0; i--)
        {
            if (!newItems.Contains(FilteredTorrents[i]))
            {
                FilteredTorrents.RemoveAt(i);
            }
        }

        // Add new items and reorder existing ones
        for (int i = 0; i < newItems.Count; i++)
        {
            var item = newItems[i];
            var currentIndex = FilteredTorrents.IndexOf(item);

            if (currentIndex == -1)
            {
                // Item doesn't exist, insert it
                FilteredTorrents.Insert(i, item);
            }
            else if (currentIndex != i)
            {
                // Item exists but in wrong position, move it
                FilteredTorrents.Move(currentIndex, i);
            }
        }
    }

    private void ApplySort()
    {
        var sorted = ApplySortToEnumerable(FilteredTorrents).ToList();

        // Reorder in place
        for (int i = 0; i < sorted.Count; i++)
        {
            var currentIndex = FilteredTorrents.IndexOf(sorted[i]);
            if (currentIndex != i)
            {
                FilteredTorrents.Move(currentIndex, i);
            }
        }
    }

    #endregion

    #region Commands

    [RelayCommand]
    private void ToggleColumnVisibility(string columnKey)
    {
        var column = ColumnDefinitions.FirstOrDefault(c => c.Key == columnKey);
        if (column == null || column.IsNameColumn) return;
        column.IsVisible = !column.IsVisible;
        SaveViewState();
    }

    [RelayCommand]
    private void SortBy(string column)
    {
        if (SortColumn == column)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = column;
            SortAscending = true;
        }
    }

    [RelayCommand]
    private void SelectTorrent(TorrentViewModel? torrent)
    {
        // Deselect previous
        if (SelectedTorrent != null)
        {
            SelectedTorrent.IsSelected = false;
        }

        SelectedTorrent = torrent;

        if (torrent != null)
        {
            torrent.IsSelected = true;
        }
    }

    [RelayCommand]
    private async Task PauseTorrentAsync(TorrentViewModel? torrent)
    {
        if (torrent == null) return;

        if (_torrentManager != null)
        {
            // Task.Run moves the synchronous preamble (engine stop, CTS.Cancel,
            // peer disconnection callbacks, event firing) off the UI thread.
            await Task.Run(() => _torrentManager.Service.PauseTorrentAsync(torrent.InfoHash)).ConfigureAwait(false);
        }
        RefreshCounts();
    }

    [RelayCommand]
    private async Task ResumeTorrentAsync(TorrentViewModel? torrent)
    {
        if (torrent == null) return;

        if (_torrentManager != null)
        {
            await Task.Run(() => _torrentManager.Service.ResumeTorrentAsync(torrent.InfoHash)).ConfigureAwait(false);
        }
        RefreshCounts();
    }

    [RelayCommand]
    private async Task ForceStartTorrentAsync(TorrentViewModel? torrent)
    {
        if (torrent == null || _torrentManager == null) return;
        await Task.Run(() => _torrentManager.Service.ForceStartAsync(torrent.InfoHash)).ConfigureAwait(false);
        RefreshCounts();
    }

    [RelayCommand]
    private async Task ForceRecheckTorrentAsync(TorrentViewModel? torrent)
    {
        if (torrent == null || _torrentManager == null) return;
        await Task.Run(() => _torrentManager.Service.ForceRecheckAsync(torrent.InfoHash)).ConfigureAwait(false);
        RefreshCounts();
    }

    [RelayCommand]
    private void SetQueuePositionTop(TorrentViewModel? torrent)
    {
        if (torrent == null || _torrentManager == null) return;
        _torrentManager.Service.SetQueuePositionTop(torrent.InfoHash);
    }

    [RelayCommand]
    private async Task ToggleSuperSeedingAsync(TorrentViewModel? torrent)
    {
        if (torrent == null || _torrentManager == null) return;
        await Task.Run(() => _torrentManager.Service.ToggleSuperSeedingAsync(torrent.InfoHash)).ConfigureAwait(false);
    }

    [RelayCommand]
    internal async Task RemoveTorrentAsync(TorrentViewModel? torrent)
    {
        if (torrent == null) return;

        Torrents.Remove(torrent);
        FilteredTorrents.Remove(torrent);
        RefreshCounts();

        if (_torrentManager != null)
        {
            await Task.Run(() => _torrentManager.Service.RemoveTorrentAsync(torrent.InfoHash)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Remove multiple torrents from the UI grid instantly (batch operation).
    /// </summary>
    public void RemoveFromGrid(IReadOnlyList<TorrentViewModel> torrents)
    {
        if (torrents.Count == 0) return;

        // For small removals, per-item is fine
        if (torrents.Count <= 3)
        {
            foreach (var torrent in torrents)
            {
                Torrents.Remove(torrent);
                FilteredTorrents.Remove(torrent);
            }
            RefreshCounts();
            return;
        }

        // For bulk removals, use set-based filter + reset to avoid
        // O(n) CollectionChanged per item. One Reset notification
        // is far cheaper than N Remove notifications for the DataGrid.
        var toRemove = new HashSet<TorrentViewModel>(torrents);

        var remainingTorrents = Torrents.Where(t => !toRemove.Contains(t)).ToList();
        Torrents.Clear();
        foreach (var t in remainingTorrents)
            Torrents.Add(t);

        var remainingFiltered = FilteredTorrents.Where(t => !toRemove.Contains(t)).ToList();
        FilteredTorrents.Clear();
        foreach (var t in remainingFiltered)
            FilteredTorrents.Add(t);

        RefreshCounts();
    }

    /// <summary>
    /// Remove a torrent from the backend only (UI already cleared via RemoveFromGrid).
    /// </summary>
    public async Task RemoveFromBackendAsync(string infoHash)
    {
        if (_torrentManager != null)
        {
            await _torrentManager.Service.RemoveTorrentAsync(infoHash).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Remove a torrent from the backend with file deletion (UI already cleared via RemoveFromGrid).
    /// </summary>
    public async Task<DeleteResult?> RemoveFromBackendWithFilesAsync(
        string infoHash, bool secureWipe = false, bool wipeMetadata = false,
        IProgress<DeletionProgress>? progress = null)
    {
        if (_torrentManager != null)
        {
            return await _torrentManager.Service.RemoveTorrentAsync(
                infoHash, deleteFiles: true, secureWipe: secureWipe,
                wipeMetadata: wipeMetadata, progress: progress)
                .ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Event raised when delete confirmation is requested.
    /// The UI handles this by showing a dialog.
    /// </summary>
    public event Action<TorrentViewModel, Action<bool>>? DeleteConfirmationRequested;

    /// <summary>
    /// Event raised when the user requests to open a torrent in the editor.
    /// The infoHash string identifies which torrent to edit.
    /// </summary>
    public event EventHandler<string>? EditTorrentRequested;

    /// <summary>
    /// Raises <see cref="EditTorrentRequested"/> for the given infoHash.
    /// </summary>
    public void RaiseEditTorrentRequested(string infoHash)
    {
        EditTorrentRequested?.Invoke(this, infoHash);
    }

    /// <summary>
    /// Request delete with confirmation dialog
    /// </summary>
    public void RequestDeleteWithConfirmation(TorrentViewModel torrent, Action<bool> callback)
    {
        DeleteConfirmationRequested?.Invoke(torrent, callback);
    }

    #endregion

    #region Public Methods

    public void AddTorrent(TorrentViewModel torrent)
    {
        Torrents.Add(torrent);
        ApplyFilter();
        RefreshCounts();
    }

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(CategoryTotalCount));
        OnPropertyChanged(nameof(DownloadingCount));
        OnPropertyChanged(nameof(SeedingCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(PausedCount));
        OnPropertyChanged(nameof(ErroredCount));
    }

    /// <summary>
    /// Toggle a tag filter on/off
    /// </summary>
    public void ToggleTagFilter(int tagId)
    {
        if (_tagFilters.Contains(tagId))
            _tagFilters.Remove(tagId);
        else
            _tagFilters.Add(tagId);

        ApplyFilter();
    }

    /// <summary>
    /// Check if a tag filter is active
    /// </summary>
    public bool IsTagFilterActive(int tagId) => _tagFilters.Contains(tagId);

    /// <summary>
    /// Clear all tag filters
    /// </summary>
    public void ClearTagFilters()
    {
        _tagFilters.Clear();
        ApplyFilter();
    }

    #endregion

    #region View State Persistence

    /// <summary>
    /// Set the persistence layer for view state
    /// </summary>
    public void SetPersistence(SessionPersistence? persistence)
    {
        _persistence = persistence;
    }

    /// <summary>
    /// The currently active ViewState, stored for column width updates.
    /// </summary>
    public ViewState? ViewState { get; private set; }

    /// <summary>
    /// Apply saved view state
    /// </summary>
    public void ApplyViewState(ViewState? viewState)
    {
        if (viewState == null)
            return;

        ViewState = viewState;

        try
        {
            _isApplyingViewState = true;

            // Apply sort settings
            if (viewState.HasValidSortColumn())
            {
                SortColumn = viewState.SortColumn;
                SortAscending = viewState.SortAscending;
            }

            // Apply column visibility
            if (viewState.ColumnVisibility.Count > 0)
            {
                foreach (var col in ColumnDefinitions)
                {
                    if (col.IsNameColumn) continue;
                    if (viewState.ColumnVisibility.TryGetValue(col.Key, out var visible))
                    {
                        col.IsVisible = visible;
                    }
                }
            }

            // Apply selection after torrents are loaded
            if (!string.IsNullOrEmpty(viewState.SelectedInfoHash))
            {
                var torrent = Torrents.FirstOrDefault(t => t.InfoHash == viewState.SelectedInfoHash);
                if (torrent != null)
                {
                    SelectTorrent(torrent);
                }
            }
        }
        finally
        {
            _isApplyingViewState = false;
        }
    }

    // Graph toggle state (set by MainWindowViewModel, included in persisted ViewState)
    public bool GraphShowDownloadLine { get; set; } = true;
    public bool GraphShowUploadLine { get; set; } = true;

    /// <summary>
    /// Get current view state for persistence
    /// </summary>
    public ViewState GetCurrentViewState()
    {
        var state = new ViewState
        {
            SortColumn = SortColumn,
            SortAscending = SortAscending,
            SelectedInfoHash = SelectedTorrent?.InfoHash,
            ActiveSection = _navigationService?.CurrentSection.ToString() ?? "Overview",
            ShowDownloadLine = GraphShowDownloadLine,
            ShowUploadLine = GraphShowUploadLine,
            ColumnWidths = ViewState?.ColumnWidths
        };

        // Only persist non-default visibility values
        foreach (var col in ColumnDefinitions)
        {
            if (col.IsNameColumn) continue;
            if (col.IsVisible != col.DefaultVisible)
            {
                state.ColumnVisibility[col.Key] = col.IsVisible;
            }
        }

        return state;
    }

    /// <summary>
    /// Save current view state to persistence
    /// </summary>
    public async void SaveViewState()
    {
        if (_persistence == null || _isApplyingViewState)
            return;

        try
        {
            var state = GetCurrentViewState();
            await _persistence.SaveViewStateAsync(state);
        }
        catch
        {
            // Ignore save errors
        }
    }

    /// <summary>
    /// Persist updated column widths without overwriting other view state fields.
    /// </summary>
    public void UpdateColumnWidths(Dictionary<string, double> widths)
    {
        if (ViewState != null)
        {
            ViewState.ColumnWidths = widths;
            _ = _persistence?.SaveViewStateAsync(ViewState);
        }
    }

    #endregion

    #region Sample Data

    private void InitializeSampleData()
    {
        var errorSnapshot = new TorrentSnapshot
        {
            Name = "⚠ Service initialization failed — check console for details",
            InfoHash = "error_fallback",
            Status = new TorrentStatus { Error = new TorrentError { Message = "Service initialization failed" } },
        };
        Torrents = new ObservableCollection<TorrentViewModel>
        {
            new TorrentViewModel(errorSnapshot)
        };
    }

    #endregion
}
