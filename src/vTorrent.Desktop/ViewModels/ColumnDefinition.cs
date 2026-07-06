using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace vTorrent.Desktop.ViewModels;

public partial class ColumnDefinition : ObservableObject
{
    public string Key { get; }
    public string Header { get; }
    public string BindingPath { get; }
    public string SortMemberPath { get; }
    public bool DefaultVisible { get; }
    public bool IsNameColumn { get; }

    /// <summary>
    /// Column width. "*" for star sizing, "Auto" for auto, or pixel value like "120".
    /// </summary>
    public string Width { get; }

    /// <summary>
    /// Minimum width in pixels (0 = no minimum).
    /// </summary>
    public double MinWidth { get; }

    [ObservableProperty]
    private bool _isVisible;

    public ColumnDefinition(string key, string header, string bindingPath, string sortMemberPath,
        bool defaultVisible, string width = "Auto", double minWidth = 0, bool isNameColumn = false)
    {
        Key = key;
        Header = header;
        BindingPath = bindingPath;
        SortMemberPath = sortMemberPath;
        DefaultVisible = defaultVisible;
        Width = width;
        MinWidth = minWidth;
        IsNameColumn = isNameColumn;
        IsVisible = defaultVisible || isNameColumn;
    }

    public static List<ColumnDefinition> CreateDefaults()
    {
        return new List<ColumnDefinition>
        {
            new("Name", "NAME", "EffectiveDisplayName", "EffectiveDisplayName", true, "*", 200, isNameColumn: true),
            new("Progress", "PROGRESS", "ProgressPercent", "Progress", true, "Auto", 150),
            new("Size", "SIZE", "Size", "TotalSize", true, "Auto", 70),
            new("ETA", "TIME LEFT", "ETADisplay", "ETA", true, "Auto", 95),
            new("Seeds", "SEEDS", "ConnectedSeeds", "ConnectedSeeds", true, "Auto", 65),
            new("Peers", "PEERS", "ConnectedPeers", "ConnectedPeers", true, "Auto", 65),
            new("Status", "STATUS", "StatusDetail", "State", false, "Auto", 80),
            new("DownSpeed", "DOWN SPEED", "DownloadSpeed", "DownloadRate", false, "Auto", 105),
            new("UpSpeed", "UP SPEED", "UploadSpeed", "UploadRate", false, "Auto", 90),
            new("Ratio", "RATIO", "RatioDisplay", "Ratio", false, "Auto", 65),
            new("Downloaded", "DOWNLOADED", "Downloaded", "TotalDone", false, "Auto", 110),
            new("Uploaded", "UPLOADED", "UploadedDisplay", "Uploaded", false, "Auto", 90),
            new("AddedOn", "ADDED ON", "AddedOnDisplay", "AddedOn", false, "Auto", 100),
            new("CompletedOn", "COMPLETED ON", "CompletedOnDisplay", "CompletedOn", false, "Auto", 120),
            new("Availability", "AVAILABILITY", "AvailabilityDisplay", "Availability", false, "Auto", 115),
            new("TimeActive", "TIME ACTIVE", "ActiveDurationDisplay", "ActiveDuration", false, "Auto", 110),
            new("SeedingTime", "SEEDING TIME", "SeedingDurationDisplay", "SeedingDuration", false, "Auto", 120),
            new("SavePath", "SAVE PATH", "SavePath", "SavePath", false, "Auto", 150),
            new("Category", "CATEGORY", "CategoryName", "CategoryName", false, "Auto", 90),
            new("Tags", "TAGS", "TagsDisplay", "TagsDisplay", false, "Auto", 70),
        };
    }
}
