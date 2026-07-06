using vTorrent.Desktop.Formatting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Bencode.Torrents;
using vTorrent.Core;
using vTorrent.Core.Session;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the Add Magnet Link dialog.
/// Handles magnet link parsing and adding to the client.
/// </summary>
public partial class AddMagnetLinkViewModel : ObservableObject
{
    private readonly ITorrentManagerService? _torrentManager;

    #region Properties - Magnet URI

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    private string _magnetUri = string.Empty;

    partial void OnMagnetUriChanged(string value)
    {
        // Auto-parse to validate and extract info
        ParseMagnetLink();
    }

    #endregion

    #region Properties - Parsed Info

    [ObservableProperty]
    private string _torrentName = string.Empty;

    [ObservableProperty]
    private string _infoHash = string.Empty;

    [ObservableProperty]
    private string _trackerCount = string.Empty;

    [ObservableProperty]
    private bool _isParsedSuccessfully;

    #endregion

    #region Properties - Save Location

    [ObservableProperty]
    private string _savePath = string.Empty;

    #endregion

    #region Properties - Options

    [ObservableProperty]
    private bool _startTorrent = true;

    [ObservableProperty]
    private bool _sequentialDownload;

    #endregion

    #region Properties - File Tree (post-metadata)

    [ObservableProperty]
    private TorrentFileTreeNodeViewModel? _fileTree;

    [ObservableProperty]
    private bool _isMetadataPhase; // false = input phase, true = file selection phase

    [ObservableProperty]
    private string _selectedSizeText = string.Empty;

    [ObservableProperty]
    private string _fileSearchText = string.Empty;

    partial void OnFileSearchTextChanged(string value)
    {
        if (FileTree == null) return;
        ApplySearchRecursive(FileTree, value.Trim());
    }

    // Store parsed BEP 53 file indices
    private int[]? _selectOnlyIndices;

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

    public bool CanAccept => IsParsedSuccessfully && !HasError && !IsLoading;

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
    public AddMagnetLinkViewModel() : this(null)
    {
        // Sample data for design time
        MagnetUri = "magnet:?xt=urn:btih:A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2&dn=Sample+Torrent";
    }

    /// <summary>
    /// Runtime constructor
    /// </summary>
    public AddMagnetLinkViewModel(ITorrentManagerService? torrentManager)
    {
        _torrentManager = torrentManager;

        // Set default save path to user's Downloads folder
        SavePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
    }

    #endregion

    #region Methods

    private void ParseMagnetLink()
    {
        ErrorMessage = null;
        IsParsedSuccessfully = false;
        TorrentName = string.Empty;
        InfoHash = string.Empty;
        TrackerCount = string.Empty;

        if (string.IsNullOrWhiteSpace(MagnetUri))
            return;

        if (!MagnetLink.IsMagnetUri(MagnetUri))
        {
            ErrorMessage = "Not a valid magnet URI";
            return;
        }

        try
        {
            var magnet = MagnetLink.Parse(MagnetUri);

            TorrentName = magnet.DisplayName ?? "Unknown";
            InfoHash = magnet.InfoHashHex ?? "Unknown";
            TrackerCount = magnet.Trackers?.Count > 0
                ? $"{magnet.Trackers.Count} tracker(s)"
                : "No trackers (DHT only)";
            IsParsedSuccessfully = true;
            _selectOnlyIndices = magnet.FileIndices?.Count > 0 ? magnet.FileIndices.ToArray() : null;

            OnPropertyChanged(nameof(CanAccept));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to parse magnet link: {ex.Message}";
        }
    }

    /// <summary>
    /// Called by the view after folder selection
    /// </summary>
    public void SetSavePath(string path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            SavePath = path;
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
    private async Task AcceptAsync()
    {
        if (!IsParsedSuccessfully)
        {
            ErrorMessage = "Please enter a valid magnet link";
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
            // Build file priorities from tree (if metadata phase)
            FilePriority[]? filePriorities = null;
            if (FileTree != null)
            {
                var collected = AddTorrentViewModel.CollectFilePriorities(FileTree);
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

            var options = new TorrentAddOptions
            {
                SavePath = SavePath,
                StartImmediately = StartTorrent,
                SequentialDownload = SequentialDownload,
                FirstLastPiecePriority = false,
                FilePriorities = filePriorities
            };

            await _torrentManager.AddMagnetLinkAsync(MagnetUri, options);

            DialogAccepted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to add magnet link: {ex.Message}";
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
    private void PasteFromClipboard()
    {
        // This will be handled by the view since we need platform-specific clipboard access
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

    #region Metadata Callback

    /// <summary>
    /// Called when torrent metadata is received. Builds the file tree and enters file selection phase.
    /// </summary>
    public void OnMetadataReceived(Torrent torrent)
    {
        IsMetadataPhase = true;
        IsLoading = false;

        // Build file tree
        var files = torrent.Info.Files.Select((f, i) => (
            fullPath: f.GetFullPath(),
            sizeBytes: f.Length,
            fileIndex: i
        ));
        FileTree = TorrentFileTreeNodeViewModel.BuildTree(torrent.DisplayName, files);
        FileTree.OnSelectionChanged = UpdateSelectedSize;

        // Apply BEP 53 pre-selection
        if (_selectOnlyIndices != null && _selectOnlyIndices.Length > 0)
        {
            var selectedSet = new HashSet<int>(_selectOnlyIndices);
            ApplyBep53Selection(FileTree, selectedSet);
        }

        UpdateSelectedSize();
    }

    private static void ApplyBep53Selection(TorrentFileTreeNodeViewModel node, HashSet<int> selectedIndices)
    {
        if (!node.IsFolder)
        {
            node.IsChecked = selectedIndices.Contains(node.FileIndex);
            return;
        }
        foreach (var child in node.Children)
            ApplyBep53Selection(child, selectedIndices);
    }

    private void UpdateSelectedSize()
    {
        if (FileTree == null) { SelectedSizeText = ""; return; }
        var bytes = FileTree.GetSelectedSizeBytes();
        SelectedSizeText = $"Selected: {FormatHelper.FormatBytesPrecise(bytes)}";
    }

    private static bool ApplySearchRecursive(TorrentFileTreeNodeViewModel node, string query)
    {
        if (!node.IsFolder)
        {
            node.IsVisible = string.IsNullOrEmpty(query) ||
                node.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            return node.IsVisible;
        }

        bool anyChildVisible = false;
        foreach (var child in node.Children)
        {
            if (ApplySearchRecursive(child, query))
                anyChildVisible = true;
        }
        node.IsVisible = anyChildVisible;
        return anyChildVisible;
    }

    #endregion
}
