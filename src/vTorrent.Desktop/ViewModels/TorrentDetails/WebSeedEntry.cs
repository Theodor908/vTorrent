using CommunityToolkit.Mvvm.ComponentModel;

namespace vTorrent.Desktop.ViewModels.TorrentDetails;

/// <summary>
/// Observable entry for the HTTP Sources tab in Torrent Details.
/// Updated per timer tick from WebSeedManager.AllSeeds.
/// </summary>
public partial class WebSeedEntry : ObservableObject
{
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _type = "";       // "BEP 19" or "BEP 17"
    [ObservableProperty] private string _status = "";     // "Active", "Idle", "Backoff (12s)", "Banned"
    [ObservableProperty] private string _dlSpeed = "";
    [ObservableProperty] private string _downloaded = "";
}
