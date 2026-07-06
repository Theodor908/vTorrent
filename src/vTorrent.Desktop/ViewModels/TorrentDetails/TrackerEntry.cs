using CommunityToolkit.Mvvm.ComponentModel;

namespace vTorrent.Desktop.ViewModels.TorrentDetails;

public partial class TrackerEntry : ObservableObject
{
    [ObservableProperty] private int _tier;
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _peers;
    [ObservableProperty] private int _seeds;
    [ObservableProperty] private int _leeches;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _responseTime = "";

    /// <summary>Key for in-place matching during refresh.</summary>
    public string Key => Url;
}
