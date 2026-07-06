using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Abstractions.Models;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Engine;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels.Tools;

public partial class TorrentCreatorViewModel : ObservableObject
{
    private readonly ITorrentManagerService? _torrentManager;

    public TorrentCreatorViewModel(ITorrentManagerService? torrentManager = null)
    {
        _torrentManager = torrentManager;
    }

    #region Content Selection

    [ObservableProperty]
    private string? _selectedPath;

    [ObservableProperty]
    private long _totalSize;

    [ObservableProperty]
    private string _totalSizeFormatted = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateTorrentCommand))]
    private bool _hasContent;

    private List<string> _filePaths = new();

    #endregion

    #region Torrent Properties

    [ObservableProperty]
    private int _selectedFormatIndex; // 0=Hybrid, 1=V1, 2=V2

    [ObservableProperty]
    private int _selectedPieceSizeIndex; // 0=Auto, 1=16KiB, 2=32KiB, ...

    [ObservableProperty]
    private string _calculatedPieceSizeText = "—";

    [ObservableProperty]
    private string? _comment;

    [ObservableProperty]
    private string? _source;

    [ObservableProperty]
    private bool _isPrivate;

    #endregion

    #region Trackers & Seeds

    [ObservableProperty]
    private string _trackersText = "";

    [ObservableProperty]
    private string _webSeedsText = "";

    #endregion

    #region Output

    [ObservableProperty]
    private bool _startSeedingImmediately = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateTorrentCommand))]
    private bool _isCreating;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressText = "";

    #endregion

    #region Dropdown Options

    public ObservableCollection<string> FormatOptions { get; } = new()
    {
        "Hybrid (V1+V2)",
        "V1 Only",
        "V2 Only",
    };

    public ObservableCollection<string> PieceSizeOptions { get; } = new()
    {
        "Auto",
        "16 KiB", "32 KiB", "64 KiB", "128 KiB", "256 KiB", "512 KiB",
        "1 MiB", "2 MiB", "4 MiB", "8 MiB", "16 MiB",
    };

    #endregion

    #region Events (for View code-behind to wire file dialogs)

    public event Func<Task<string?>>? BrowseFileRequested;
    public event Func<Task<string?>>? BrowseFolderRequested;
    public event Func<string, Task<string?>>? SaveFileRequested;

    #endregion

    [RelayCommand]
    private async Task AddFile()
    {
        if (BrowseFileRequested == null) return;
        var path = await BrowseFileRequested.Invoke();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        _filePaths = new List<string> { path };
        SelectedPath = path;
        TotalSize = new FileInfo(path).Length;
        TotalSizeFormatted = FormatSize(TotalSize);
        HasContent = true;
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        if (BrowseFolderRequested == null) return;
        var path = await BrowseFolderRequested.Invoke();
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        // BEP 52: files sorted by ordinal for V2 hash stability
        _filePaths = Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        SelectedPath = path;
        TotalSize = _filePaths.Sum(f => new FileInfo(f).Length);
        TotalSizeFormatted = FormatSize(TotalSize);
        HasContent = _filePaths.Count > 0;
    }

    /// <summary>
    /// Set content from a drag-and-drop path (file or folder).
    /// </summary>
    public void SetContentPath(string path)
    {
        if (Directory.Exists(path))
        {
            _filePaths = Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            SelectedPath = path;
        }
        else if (File.Exists(path))
        {
            _filePaths = new List<string> { path };
            SelectedPath = path;
        }
        else return;

        TotalSize = _filePaths.Sum(f => new FileInfo(f).Length);
        TotalSizeFormatted = FormatSize(TotalSize);
        HasContent = _filePaths.Count > 0;
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateTorrent()
    {
        if (_filePaths.Count == 0) return;

        IsCreating = true;
        ProgressPercent = 0;
        ProgressText = "Hashing files...";
        CreateTorrentCommand.NotifyCanExecuteChanged();

        try
        {
            var mode = SelectedFormatIndex switch
            {
                1 => TorrentCreator.CreateMode.V1,
                2 => TorrentCreator.CreateMode.V2,
                _ => TorrentCreator.CreateMode.Hybrid,
            };

            var trackerTiers = ParseTrackerTiers(TrackersText);
            var webSeeds = ParseLines(WebSeedsText);
            var name = Path.GetFileName(SelectedPath) ?? "torrent";

            var options = new TorrentCreateOptions
            {
                Name = name,
                FilePaths = _filePaths,
                Mode = mode,
                PieceLength = GetSelectedPieceLength(),
                TrackerTiers = trackerTiers.Count > 0 ? trackerTiers : null,
                IsPrivate = IsPrivate,
                Source = string.IsNullOrWhiteSpace(Source) ? null : Source,
                Comment = string.IsNullOrWhiteSpace(Comment) ? null : Comment,
                UrlSeeds = webSeeds.Count > 0 ? webSeeds : null,
            };

            var progress = new Progress<TorrentCreator.TorrentCreateProgress>(p =>
            {
                if (p.TotalBytes > 0)
                    ProgressPercent = (double)p.BytesHashed / p.TotalBytes * 100;
                ProgressText = $"Hashing: {p.CurrentFile}";
            });

            var torrent = await TorrentCreator.CreateAsync(options, progress);

            // Prompt for save location
            var suggestedName = name + ".torrent";
            var savePath = SaveFileRequested != null
                ? await SaveFileRequested.Invoke(suggestedName)
                : null;

            if (string.IsNullOrEmpty(savePath))
            {
                ProgressText = "Cancelled.";
                return;
            }

            // Write .torrent file
            var dict = torrent.ToBDictionary();
            dict.EncodeToFile(savePath);

            ProgressPercent = 100;
            ProgressText = $"Created: {Path.GetFileName(savePath)}";

            // Start seeding if requested
            if (StartSeedingImmediately && _torrentManager?.Service != null)
            {
                var contentPath = Path.GetDirectoryName(SelectedPath) ?? SelectedPath!;
                await _torrentManager.Service.AddTorrentAsync(savePath, new TorrentAddOptions
                {
                    SavePath = contentPath,
                    StartImmediately = true,
                    SeedMode = true,
                });
                ProgressText += " — seeding started";
            }
        }
        catch (Exception ex)
        {
            ProgressText = $"Error: {ex.Message}";
        }
        finally
        {
            IsCreating = false;
            CreateTorrentCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCreate() => HasContent && !IsCreating;

    #region Helpers

    private long? GetSelectedPieceLength()
    {
        if (SelectedPieceSizeIndex == 0) return null; // Auto
        return 16384L << (SelectedPieceSizeIndex - 1);
    }

    private static List<IReadOnlyList<string>> ParseTrackerTiers(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();

        var tiers = new List<IReadOnlyList<string>>();
        var currentTier = new List<string>();

        foreach (var line in text.Split('\n', StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                if (currentTier.Count > 0)
                {
                    tiers.Add(currentTier.AsReadOnly());
                    currentTier = new List<string>();
                }
            }
            else
            {
                currentTier.Add(trimmed);
            }
        }

        if (currentTier.Count > 0)
            tiers.Add(currentTier.AsReadOnly());

        return tiers;
    }

    private static List<string> ParseLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KiB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MiB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GiB",
    };

    #endregion
}
