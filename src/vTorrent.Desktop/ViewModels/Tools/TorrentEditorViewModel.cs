using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Torrents;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels.Tools;

public partial class TorrentEditorViewModel : ObservableObject
{
    private readonly ITorrentManagerService? _torrentManager;

    #region Mode

    [ObservableProperty]
    private bool _isInListMode = true;

    [RelayCommand]
    private void SwitchToInList() => IsInListMode = true;

    [RelayCommand]
    private void SwitchToFromFile() => IsInListMode = false;

    #endregion

    #region In-List Mode

    /// <summary>Simplified torrent entries for the selector ComboBox.</summary>
    [ObservableProperty]
    private ObservableCollection<TorrentSelectorItem> _torrentItems = new();

    [ObservableProperty]
    private TorrentSelectorItem? _selectedTorrentItem;

    [ObservableProperty]
    private string _inListDisplayName = "";

    [ObservableProperty]
    private string _inListTrackersText = "";

    [ObservableProperty]
    private string _inListWebSeedsText = "";

    [ObservableProperty]
    private string _inListInfoText = "";

    [ObservableProperty]
    private bool _hasSelectedTorrent;

    #endregion

    #region From-File Mode

    [ObservableProperty]
    private string? _loadedFilePath;

    private BDictionary? _loadedDict;

    [ObservableProperty]
    private string _fileName = "";

    [ObservableProperty]
    private string _fileComment = "";

    [ObservableProperty]
    private string? _fileSource;

    [ObservableProperty]
    private bool _fileIsPrivate;

    [ObservableProperty]
    private string _fileTrackersText = "";

    [ObservableProperty]
    private string _fileWebSeedsText = "";

    [ObservableProperty]
    private string _fileInfoHashV1 = "";

    [ObservableProperty]
    private string? _fileInfoHashV2;

    [ObservableProperty]
    private string _fileReadOnlyInfo = "";

    [ObservableProperty]
    private bool _hasLoadedFile;

    #endregion

    #region Events

    public event Func<Task<string?>>? BrowseTorrentFileRequested;
    public event Func<string, Task<string?>>? SaveTorrentFileRequested;

    #endregion

    public TorrentEditorViewModel(ITorrentManagerService? torrentManager = null)
    {
        _torrentManager = torrentManager;
    }

    public void Initialize(string? preselectedInfoHash = null)
    {
        LoadTorrentItems();

        if (preselectedInfoHash != null)
        {
            IsInListMode = true;
            SelectedTorrentItem = TorrentItems
                .FirstOrDefault(t => t.InfoHash == preselectedInfoHash);
        }
    }

    private void LoadTorrentItems()
    {
        TorrentItems.Clear();
        var torrents = _torrentManager?.Torrents;
        if (torrents == null) return;

        foreach (var tvm in torrents)
        {
            TorrentItems.Add(new TorrentSelectorItem
            {
                InfoHash = tvm.InfoHash,
                Name = tvm.Name,
            });
        }
    }

    partial void OnSelectedTorrentItemChanged(TorrentSelectorItem? value)
    {
        if (value == null)
        {
            HasSelectedTorrent = false;
            return;
        }

        // Fetch full details for selected torrent (includes trackers, web seeds)
        var details = _torrentManager?.Service?.GetTorrentDetails(value.InfoHash);
        if (details == null)
        {
            HasSelectedTorrent = false;
            return;
        }

        HasSelectedTorrent = true;

        // Read display name from the VM (which has the cached overlay) rather than
        // ManagedTorrentView.DisplayName (which is always null — see ManagedTorrent.ToView()).
        var vm = _torrentManager?.GetTorrentViewModel(value.InfoHash);
        InListDisplayName = vm?.EffectiveDisplayName ?? details.Name;

        // Reconstruct tracker text from TrackerInfoView[] grouped by tier
        var grouped = details.Trackers
            .GroupBy(t => t.Tier)
            .OrderBy(g => g.Key);
        var lines = new List<string>();
        bool first = true;
        foreach (var tier in grouped)
        {
            if (!first) lines.Add("");
            first = false;
            foreach (var tracker in tier)
                lines.Add(tracker.Url);
        }
        InListTrackersText = string.Join("\n", lines);

        // Web seeds
        InListWebSeedsText = string.Join("\n", details.WebSeeds.Select(ws => ws.Url));

        // Read-only info
        InListInfoText = $"Size: {FormatSize(details.TotalSize)} | Pieces: {details.PieceCount} x {FormatSize(details.PieceSize)} | Private: {(details.IsPrivate ? "Yes" : "No")} | Source: {details.Source ?? "\u2014"}";
    }

    #region In-List Save

    [ObservableProperty]
    private string _inListStatusMessage = "";

    [RelayCommand]
    private async Task SaveInListChanges()
    {
        if (SelectedTorrentItem == null || _torrentManager?.Service == null)
        {
            InListStatusMessage = "No torrent selected or service unavailable.";
            return;
        }

        var infoHash = SelectedTorrentItem.InfoHash;
        var parts = new List<string>();

        // Display name — persisted in per-torrent settings
        var settingsManager = _torrentManager.SettingsManager;
        if (settingsManager != null)
        {
            var settings = await settingsManager.GetTorrentSettingsAsync(infoHash)
                ?? new TorrentSettings { InfoHash = infoHash };
            var newName = string.IsNullOrWhiteSpace(InListDisplayName) ? null : InListDisplayName.Trim();
            if (settings.DisplayName != newName)
            {
                settings.DisplayName = newName;
                await settingsManager.SaveTorrentSettingsAsync(settings);
                parts.Add("Display name");
                await _torrentManager.RefreshDisplayNameAsync(infoHash);
            }
        }

        // Trackers — diff against running engine
        var trackerUrls = ParseLines(InListTrackersText);
        var (tAdded, tRemoved) = await _torrentManager.Service.UpdateTorrentTrackers(infoHash, trackerUrls);
        if (tAdded > 0 || tRemoved > 0)
            parts.Add($"Trackers: +{tAdded} −{tRemoved}");

        // Web seeds — diff against running engine
        var webSeedUrls = ParseLines(InListWebSeedsText);
        var (wAdded, wRemoved) = await _torrentManager.Service.UpdateTorrentWebSeeds(infoHash, webSeedUrls);
        if (wAdded > 0 || wRemoved > 0)
            parts.Add($"Web seeds: +{wAdded} −{wRemoved}");

        InListStatusMessage = parts.Count > 0
            ? $"Saved: {string.Join(", ", parts)}."
            : "No changes detected.";
    }

    #endregion

    #region From-File Commands

    [RelayCommand]
    private async Task BrowseFile()
    {
        if (BrowseTorrentFileRequested == null) return;
        var path = await BrowseTorrentFileRequested.Invoke();
        if (string.IsNullOrEmpty(path)) return;

        LoadTorrentFile(path);
    }

    private void LoadTorrentFile(string path)
    {
        try
        {
            _loadedDict = TorrentEditor.LoadFromFile(path);
            LoadedFilePath = path;

            var metadata = TorrentEditor.GetEditableMetadata(_loadedDict);
            _suppressHashRecalc = true;
            FileName = metadata.Name;
            FileComment = metadata.Comment ?? "";
            FileSource = metadata.Source;
            FileIsPrivate = metadata.IsPrivate;
            _suppressHashRecalc = false;

            // Reconstruct tracker text
            var lines = new List<string>();
            bool first = true;
            foreach (var tier in metadata.Trackers)
            {
                if (!first) lines.Add("");
                first = false;
                lines.AddRange(tier);
            }
            FileTrackersText = string.Join("\n", lines);
            FileWebSeedsText = string.Join("\n", metadata.UrlSeeds);

            // Read-only info
            var ro = TorrentEditor.GetReadOnlyMetadata(_loadedDict);
            FileInfoHashV1 = ro.InfoHashV1 ?? "\u2014";
            FileInfoHashV2 = ro.InfoHashV2;
            FileReadOnlyInfo = $"Size: {FormatSize(ro.TotalSize)} | Pieces: {ro.PieceCount} x {FormatSize(ro.PieceSize)} | Format: {ro.Format} | Files: {ro.FileCount}";

            HasLoadedFile = true;
        }
        catch (Exception ex)
        {
            FileReadOnlyInfo = $"Error loading file: {ex.Message}";
            HasLoadedFile = false;
        }
    }

    [RelayCommand]
    private void SaveFile()
    {
        if (_loadedDict == null || string.IsNullOrEmpty(LoadedFilePath)) return;
        ApplyFileChanges();
        TorrentEditor.SaveToFile(_loadedDict, LoadedFilePath);
        RefreshFileHashes();
    }

    [RelayCommand]
    private async Task SaveFileAs()
    {
        if (_loadedDict == null || SaveTorrentFileRequested == null) return;
        ApplyFileChanges();

        var suggestedName = System.IO.Path.GetFileName(LoadedFilePath) ?? "edited.torrent";
        var path = await SaveTorrentFileRequested.Invoke(suggestedName);
        if (string.IsNullOrEmpty(path)) return;

        TorrentEditor.SaveToFile(_loadedDict, path);
        LoadedFilePath = path;
        RefreshFileHashes();
    }

    private void ApplyFileChanges()
    {
        if (_loadedDict == null) return;

        var metadata = new TorrentEditableMetadata
        {
            Name = FileName,
            Comment = string.IsNullOrWhiteSpace(FileComment) ? null : FileComment,
            Source = string.IsNullOrWhiteSpace(FileSource) ? null : FileSource,
            IsPrivate = FileIsPrivate,
            Trackers = ParseTrackerTiers(FileTrackersText),
            UrlSeeds = ParseLines(FileWebSeedsText),
        };

        TorrentEditor.ApplyChanges(_loadedDict, metadata);
    }

    // Live info hash recalculation when info-dict fields change
    private bool _suppressHashRecalc;

    private void RefreshFileHashes()
    {
        if (_loadedDict == null || _suppressHashRecalc) return;
        ApplyFileChanges();
        var (v1, v2) = TorrentEditor.RecalculateInfoHashes(_loadedDict);
        FileInfoHashV1 = v1 ?? "\u2014";
        FileInfoHashV2 = v2;
    }

    partial void OnFileNameChanged(string value) => RefreshFileHashes();
    partial void OnFileSourceChanged(string? value) => RefreshFileHashes();
    partial void OnFileIsPrivateChanged(bool value) => RefreshFileHashes();

    #endregion

    #region Helpers

    private static List<List<string>> ParseTrackerTiers(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();

        var tiers = new List<List<string>>();
        var currentTier = new List<string>();

        foreach (var line in text.Split('\n', StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                if (currentTier.Count > 0)
                {
                    tiers.Add(currentTier);
                    currentTier = new List<string>();
                }
            }
            else
            {
                currentTier.Add(trimmed);
            }
        }

        if (currentTier.Count > 0)
            tiers.Add(currentTier);

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

/// <summary>
/// Lightweight item for the torrent selector ComboBox (avoids fetching full details for all torrents).
/// </summary>
public sealed class TorrentSelectorItem
{
    public string InfoHash { get; init; } = "";
    public string Name { get; init; } = "";
    public override string ToString() => $"{Name}  ({InfoHash[..Math.Min(8, InfoHash.Length)]}...)";
}
