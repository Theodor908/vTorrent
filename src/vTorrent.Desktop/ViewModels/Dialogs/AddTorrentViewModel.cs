using vTorrent.Desktop.Formatting;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Session;
using vTorrent.Storage;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the Add Torrent dialog.
/// Handles torrent file parsing, configuration, and adding to the client.
/// </summary>
public partial class AddTorrentViewModel : ObservableObject
{
    private readonly ITorrentManagerService? _torrentManager;
    private Torrent? _torrent;
    private string? _torrentFilePath;

    #region Properties - Save Location

    [ObservableProperty]
    private string _savePath = string.Empty;

    #endregion

    #region Properties - Torrent Settings

    [ObservableProperty]
    private ObservableCollection<CategoryItemViewModel> _categories = new();

    [ObservableProperty]
    private CategoryItemViewModel? _selectedCategory;

    [ObservableProperty]
    private ObservableCollection<TagItemViewModel> _tags = new();

    /// <summary>
    /// Whether there are any tags available to select
    /// </summary>
    public bool HasTags => Tags.Count > 0;

    [ObservableProperty]
    private bool _startTorrent = true;

    [ObservableProperty]
    private bool _addToTopOfQueue;

    [ObservableProperty]
    private bool _sequentialDownload;

    [ObservableProperty]
    private bool _firstLastPiecePriority;

    #endregion

    #region Properties - Torrent Information

    [ObservableProperty]
    private string _torrentName = string.Empty;

    [ObservableProperty]
    private string _totalSize = string.Empty;

    [ObservableProperty]
    private string _creationDate = string.Empty;

    [ObservableProperty]
    private string _infoHashV1 = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoHashV2Display))]
    private string _infoHashV2 = string.Empty;

    [ObservableProperty]
    private string _comment = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProtocolVersion))]
    private Bencode.Torrents.TorrentVersion _torrentVersionValue = Bencode.Torrents.TorrentVersion.V1;

    public string InfoHashV2Display => string.IsNullOrEmpty(InfoHashV2) ? "N/A" : InfoHashV2;

    public string ProtocolVersion => TorrentVersionValue switch
    {
        Bencode.Torrents.TorrentVersion.V1 => "v1",
        Bencode.Torrents.TorrentVersion.V2 => "v2",
        Bencode.Torrents.TorrentVersion.Hybrid => "Hybrid v1+v2",
        _ => "Unknown"
    };

    #endregion

    #region Properties - File Tree

    [ObservableProperty]
    private TorrentFileTreeNodeViewModel? _fileTree;

    [ObservableProperty]
    private string _fileSearchText = string.Empty;

    [ObservableProperty]
    private string _selectedSizeText = string.Empty;

    partial void OnFileSearchTextChanged(string value)
    {
        ApplyFileSearch(value);
    }

    #endregion

    #region Properties - Dialog State

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasError;

    partial void OnErrorMessageChanged(string? value)
    {
        HasError = !string.IsNullOrEmpty(value);
    }

    #endregion

    #region Events

    /// <summary>
    /// Raised when the dialog should close with OK result
    /// </summary>
    public event EventHandler? DialogAccepted;

    /// <summary>
    /// Raised when the dialog should close with Cancel result
    /// </summary>
    public event EventHandler? DialogCancelled;

    /// <summary>
    /// Raised when folder browser should be shown
    /// </summary>
    public event EventHandler? BrowseFolderRequested;

    #endregion

    #region Constructor

    /// <summary>
    /// Design-time constructor
    /// </summary>
    public AddTorrentViewModel() : this(null)
    {
        // Add sample data for design time
        TorrentName = "Sample Torrent Name";
        TotalSize = "1.5 GB";
        CreationDate = "2024-01-15";
        InfoHashV1 = "A1B2C3D4E5F6G7H8I9J0K1L2M3N4O5P6Q7R8S9T0";
        Comment = "This is a sample torrent comment";

        // Build a sample file tree for design time
        FileTree = TorrentFileTreeNodeViewModel.BuildTree("Sample Torrent", new[]
        {
            ("folder/file1.mp4", 734003200L, 0),
            ("folder/file2.mp4", 838860800L, 1),
        });
        FileTree.OnSelectionChanged = UpdateSelectedSize;
        UpdateSelectedSize();
    }

    /// <summary>
    /// Runtime constructor
    /// </summary>
    public AddTorrentViewModel(ITorrentManagerService? torrentManager)
    {
        _torrentManager = torrentManager;

        // Set default save path to user's Downloads folder
        SavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Initialize the dialog - load categories and tags
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_torrentManager == null) return;

        try
        {
            // Load categories
            var categories = await _torrentManager.Service.GetAllCategoriesAsync();
            Categories.Clear();

            // Add "None" option
            Categories.Add(new CategoryItemViewModel
            {
                Id = null,
                Name = "None",
                IsSelected = true
            });
            SelectedCategory = Categories[0];

            foreach (var category in categories)
            {
                Categories.Add(new CategoryItemViewModel
                {
                    Id = category.Id,
                    Name = category.Name,
                    Color = category.Color,
                    SavePath = category.SavePath
                });
            }

            // Load tags
            var tags = await _torrentManager.Service.GetAllTagsAsync();
            Tags.Clear();
            foreach (var tag in tags)
            {
                Tags.Add(new TagItemViewModel
                {
                    Id = tag.Id,
                    Name = tag.Name,
                    Color = tag.Color,
                    IsSelected = false
                });
            }

            // Notify HasTags changed
            OnPropertyChanged(nameof(HasTags));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load categories/tags: {ex.Message}";
        }
    }

    /// <summary>
    /// Load a torrent file and parse its contents
    /// </summary>
    public async Task LoadTorrentFileAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            ErrorMessage = "Invalid torrent file path";
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            _torrentFilePath = filePath;

            // Parse torrent file on background thread to avoid UI freeze
            var (torrent, fileEntries) = await Task.Run(() =>
            {
                // Read and parse the torrent file
                var bytes = File.ReadAllBytes(filePath);
                var parser = new BencodeParser();
                var parsed = parser.Parse(bytes, out _);

                if (parsed is not BDictionary dict)
                {
                    return (null, (List<(string, long, int)>?)null);
                }

                var t = Torrent.FromBDictionary(dict);

                // Build file entry list in memory (not on UI thread)
                var entries = t.Info.Files.Select((file, index) => (
                    fullPath: file.GetFullPath(),
                    sizeBytes: file.Length,
                    fileIndex: index
                )).ToList();

                return ((Torrent?)t, (List<(string, long, int)>?)entries);
            });

            if (torrent == null || fileEntries == null)
            {
                ErrorMessage = "Invalid torrent file format";
                return;
            }

            _torrent = torrent;

            // Populate torrent information (UI thread)
            TorrentName = _torrent.DisplayName;
            TotalSize = FormatBytes(_torrent.TotalSize);
            CreationDate = _torrent.CreationDate?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "Unknown";
            InfoHashV1 = _torrent.GetInfoHashHex();
            var infoHash = _torrent.GetInfoHash();
            InfoHashV2 = infoHash.HasV2 ? infoHash.V2!.Value.ToHex() : string.Empty;
            TorrentVersionValue = _torrent.Info.Version;
            Comment = _torrent.Comment ?? string.Empty;

            // Build hierarchical file tree
            FileTree = TorrentFileTreeNodeViewModel.BuildTree(_torrent.DisplayName, fileEntries);
            FileTree.OnSelectionChanged = UpdateSelectedSize;
            UpdateSelectedSize();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to parse torrent: {ex.Message}";
            _torrent = null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    #endregion

    #region Commands

    [RelayCommand]
    private void BrowseFolder()
    {
        BrowseFolderRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the view after folder selection.
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

    [RelayCommand]
    private async Task AcceptAsync()
    {
        if (_torrent == null || string.IsNullOrEmpty(_torrentFilePath))
        {
            ErrorMessage = "No torrent file loaded";
            return;
        }

        if (string.IsNullOrEmpty(SavePath))
        {
            ErrorMessage = "Please select a save location";
            return;
        }

        if (_torrentManager == null)
        {
            DialogAccepted?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // Determine save path (use category path if selected and available)
            var effectiveSavePath = SavePath;
            if (SelectedCategory?.SavePath != null)
            {
                effectiveSavePath = SelectedCategory.SavePath;
            }

            // Build file priorities from tree
            FilePriority[]? filePriorities = null;
            if (FileTree != null)
            {
                var collected = CollectFilePriorities(FileTree);
                if (collected.Any(fp => fp.priority != FilePriority.Normal))
                {
                    filePriorities = new FilePriority[collected.Count];
                    foreach (var (fileIndex, priority) in collected)
                    {
                        if (fileIndex >= 0 && fileIndex < filePriorities.Length)
                            filePriorities[fileIndex] = priority;
                    }
                }
            }

            // Add the torrent with options
            var options = new TorrentAddOptions
            {
                SavePath = effectiveSavePath,
                StartImmediately = StartTorrent,
                SequentialDownload = SequentialDownload,
                FirstLastPiecePriority = FirstLastPiecePriority,
                AddToTopOfQueue = AddToTopOfQueue,
                FilePriorities = filePriorities
            };

            var torrentVm = await _torrentManager.AddTorrentAsync(_torrentFilePath, options);

            // Set category if selected
            if (SelectedCategory?.Id != null)
            {
                await _torrentManager.Service.SetTorrentCategoryAsync(torrentVm.InfoHash, SelectedCategory.Id);
            }

            // Set selected tags
            var selectedTagIds = Tags.Where(t => t.IsSelected).Select(t => t.Id).ToList();
            if (selectedTagIds.Count > 0)
            {
                await _torrentManager.Service.SetTorrentTagsAsync(torrentVm.InfoHash, selectedTagIds);
            }

            // Handle AddToTopOfQueue - move torrent to top of download queue
            if (AddToTopOfQueue)
            {
                _torrentManager.Service.SetQueuePositionTop(torrentVm.InfoHash);
            }

            DialogAccepted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to add torrent: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogCancelled?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleTag(TagItemViewModel? tag)
    {
        if (tag != null)
        {
            tag.IsSelected = !tag.IsSelected;
        }
    }

    [RelayCommand]
    private void SelectAllFiles()
    {
        if (FileTree != null) FileTree.IsChecked = true;
        UpdateSelectedSize();
    }

    [RelayCommand]
    private void SelectNoFiles()
    {
        if (FileTree != null) FileTree.IsChecked = false;
        UpdateSelectedSize();
    }

    #endregion

    #region File Tree Helpers

    private void UpdateSelectedSize()
    {
        if (FileTree == null) { SelectedSizeText = ""; return; }
        var selectedBytes = FileTree.GetSelectedSizeBytes();
        SelectedSizeText = $"Selected: {FormatBytes(selectedBytes)}";

        // Update the Size field in the torrent info panel to reflect selected size
        if (_torrent != null)
        {
            if (selectedBytes == _torrent.TotalSize)
                TotalSize = FormatBytes(_torrent.TotalSize);
            else
                TotalSize = $"{FormatBytes(selectedBytes)} / {FormatBytes(_torrent.TotalSize)}";
        }
    }

    private void ApplyFileSearch(string query)
    {
        if (FileTree == null) return;
        ApplySearchRecursive(FileTree, query.Trim());
    }

    private static bool ApplySearchRecursive(TorrentFileTreeNodeViewModel node, string query)
    {
        // No query — show everything, restore default expansion
        if (string.IsNullOrEmpty(query))
        {
            node.IsVisible = true;
            node.IsExpanded = true;
            if (node.IsFolder)
            {
                foreach (var child in node.Children)
                    ApplySearchRecursive(child, query);
            }
            return true;
        }

        if (!node.IsFolder)
        {
            node.IsVisible = node.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            return node.IsVisible;
        }

        // Folder name matches — show it and all its contents
        bool folderNameMatches = node.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (folderNameMatches)
        {
            ShowAllDescendants(node);
            node.IsExpanded = true;
            return true;
        }

        // Folder name doesn't match — recurse into children
        bool anyChildVisible = false;
        foreach (var child in node.Children)
        {
            if (ApplySearchRecursive(child, query))
                anyChildVisible = true;
        }
        node.IsVisible = anyChildVisible;
        node.IsExpanded = anyChildVisible;
        return anyChildVisible;
    }

    private static void ShowAllDescendants(TorrentFileTreeNodeViewModel node)
    {
        node.IsVisible = true;
        if (node.IsFolder)
        {
            node.IsExpanded = true;
            foreach (var child in node.Children)
                ShowAllDescendants(child);
        }
    }

    internal static List<(int fileIndex, FilePriority priority)> CollectFilePriorities(TorrentFileTreeNodeViewModel node)
    {
        var result = new List<(int, FilePriority)>();
        CollectFilePrioritiesRecursive(node, result);
        return result;
    }

    private static void CollectFilePrioritiesRecursive(
        TorrentFileTreeNodeViewModel node,
        List<(int fileIndex, FilePriority priority)> result)
    {
        if (!node.IsFolder)
        {
            result.Add((node.FileIndex, node.Priority));
            return;
        }
        foreach (var child in node.Children)
            CollectFilePrioritiesRecursive(child, result);
    }

    #endregion

    #region Category Selection Changed

    partial void OnSelectedCategoryChanged(CategoryItemViewModel? value)
    {
        // Update save path based on category selection
        if (value?.Id == null)
        {
            // "None" selected - use default save path
            var defaultPath = _torrentManager?.SettingsManager?.Current.Disk.DefaultSavePath;
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

    #endregion

    #region Helpers

    private static string FormatBytes(long bytes) => FormatHelper.FormatBytesPrecise(bytes);

    #endregion
}

#region Supporting ViewModels

/// <summary>
/// ViewModel for category selection in the Add Torrent dialog
/// </summary>
public partial class CategoryItemViewModel : ObservableObject
{
    public int? Id { get; set; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string? _color;

    [ObservableProperty]
    private string? _savePath;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// ViewModel for tag selection in the Add Torrent dialog
/// </summary>
public partial class TagItemViewModel : ObservableObject
{
    public int Id { get; set; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string? _color;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// ViewModel for file items in the Add Torrent dialog
/// </summary>
public partial class TorrentFileItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _fullPath = string.Empty;

    [ObservableProperty]
    private string _size = string.Empty;

    public long SizeBytes { get; set; }
}

#endregion
