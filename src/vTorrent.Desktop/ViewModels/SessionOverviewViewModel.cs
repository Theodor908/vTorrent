using CommunityToolkit.Mvvm.ComponentModel;
using vTorrent.Desktop.Formatting;
using vTorrent.Abstractions.Models;

namespace vTorrent.Desktop.ViewModels;

/// <summary>
/// Observable wrapper around a Core SessionOverview DTO.
/// All formatted display properties live here exclusively.
/// </summary>
public partial class SessionOverviewViewModel : ObservableObject
{
    private SessionOverview _overview = new();

    // --- Raw data delegation ---
    public int GlobalDownloadRate => _overview.GlobalDownloadRate;
    public int GlobalUploadRate => _overview.GlobalUploadRate;
    public int TotalTorrents => _overview.TotalTorrents;
    public int ActiveDownloads => _overview.ActiveDownloads;
    public int ActiveUploads => _overview.ActiveUploads;
    public int PausedTorrents => _overview.PausedTorrents;
    public int CheckingTorrents => _overview.CheckingTorrents;
    public int QueuedTorrents => _overview.QueuedTorrents;
    public int ErrorTorrents => _overview.ErrorTorrents;
    public int DhtNodes => _overview.DhtNodes;
    public bool DhtEnabled => _overview.DhtEnabled;
    public int ListenPort => _overview.ListenPort;
    public bool PortOpen => _overview.PortOpen;
    public int ConnectedPeers => _overview.ConnectedPeers;
    public int TotalConnections => _overview.TotalConnections;
    public int HalfOpenConnections => _overview.HalfOpenConnections;
    public int DiskReadQueue => _overview.DiskReadQueue;
    public int DiskWriteQueue => _overview.DiskWriteQueue;
    public long DiskBytesRead => _overview.DiskBytesRead;
    public long DiskBytesWritten => _overview.DiskBytesWritten;
    public string? ExternalIp => _overview.ExternalIp;
    public int DownloadLimit => _overview.DownloadLimit;
    public int UploadLimit => _overview.UploadLimit;
    public bool IsPaused => _overview.IsPaused;
    public long FreeSpace => _overview.FreeSpace;

    // --- Formatted display (Desktop-only) ---
    public string GlobalDownloadSpeed => FormatHelper.FormatSpeed(_overview.GlobalDownloadRate);
    public string GlobalUploadSpeed => FormatHelper.FormatSpeed(_overview.GlobalUploadRate);
    public string SessionDownloadedDisplay => FormatHelper.FormatBytes(_overview.SessionDownloaded);
    public string SessionUploadedDisplay => FormatHelper.FormatBytes(_overview.SessionUploaded);
    public string AllTimeDownloadedDisplay => FormatHelper.FormatBytes(_overview.AllTimeDownloaded);
    public string AllTimeUploadedDisplay => FormatHelper.FormatBytes(_overview.AllTimeUploaded);
    public string StatusBarText => $"D: {GlobalDownloadSpeed}  U: {GlobalUploadSpeed}  " +
                                   $"Peers: {_overview.ConnectedPeers}  DHT: {_overview.DhtNodes}";
    public string UptimeDisplay => FormatHelper.FormatDuration(_overview.Uptime);
    public string FreeSpaceDisplay => FormatHelper.FormatBytes(_overview.FreeSpace);
    public bool FreeSpaceWarning => _overview.FreeSpace > 0 && _overview.FreeSpace < 1_073_741_824; // < 1 GB
    public string TorrentCountsDisplay => $"{TotalTorrents} total ({ActiveDownloads} DL, {ActiveUploads} UL, {PausedTorrents} paused)";
    public string ConnectionsDisplay => $"{ConnectedPeers} peers ({HalfOpenConnections} connecting)";
    public string DhtStatus => DhtEnabled ? $"DHT: {DhtNodes} nodes" : "DHT: disabled";
    public string PortStatus => PortOpen ? $"Port {ListenPort} (open)" : $"Port {ListenPort} (closed)";
    public string DownloadLimitDisplay => DownloadLimit > 0 ? FormatHelper.FormatSpeed(DownloadLimit) : "∞";
    public string UploadLimitDisplay => UploadLimit > 0 ? FormatHelper.FormatSpeed(UploadLimit) : "∞";
    public string SessionStatus => IsPaused ? "Session Paused" : "Running";
    public string DiskQueueDisplay => $"R: {DiskReadQueue}  W: {DiskWriteQueue}";

    // --- Bulk update ---
    public void Update(SessionOverview overview)
    {
        _overview = overview;
        OnPropertyChanged(string.Empty);
    }

    // --- Torrent count update (iterates Desktop ViewModels) ---
    public void UpdateTorrentCounts(IReadOnlyList<TorrentViewModel> viewModels)
    {
        // Count by display state from the VM list
        // This supplements the SessionOverview with Desktop-derived counts
    }
}
