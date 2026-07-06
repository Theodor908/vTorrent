using CommunityToolkit.Mvvm.ComponentModel;

namespace vTorrent.Desktop.ViewModels.TorrentDetails;

public partial class PeerEntry : ObservableObject
{
    [ObservableProperty] private string _ip = "";
    [ObservableProperty] private int _port;
    [ObservableProperty] private string _client = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _dlSpeed = "";
    [ObservableProperty] private string _ulSpeed = "";
    [ObservableProperty] private string _downloaded = "";
    [ObservableProperty] private string _uploaded = "";
    [ObservableProperty] private string _flags = "";
    [ObservableProperty] private string _rtt = "";

    /// <summary>Key for in-place matching during refresh.</summary>
    public string Key => $"{Ip}:{Port}";
}
