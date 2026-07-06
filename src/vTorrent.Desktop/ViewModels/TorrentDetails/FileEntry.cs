using CommunityToolkit.Mvvm.ComponentModel;

namespace vTorrent.Desktop.ViewModels.TorrentDetails;

public partial class FileEntry : ObservableObject
{
    /// <summary>Stable key — set once at creation, never changes.</summary>
    public int FileIndex { get; init; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _size = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _priority = "";
    [ObservableProperty] private string _availability = "";
}
