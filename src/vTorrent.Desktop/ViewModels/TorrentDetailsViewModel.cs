using vTorrent.Desktop.Formatting;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Abstractions.Models;
using vTorrent.Desktop.ViewModels.TorrentDetails;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels;

public partial class TorrentDetailsViewModel : ObservableObject, IDisposable
{
    private readonly string _torrentInfoHash;
    private readonly ITorrentManagerService _service;
    private readonly DispatcherTimer _refreshTimer;
    private bool _disposed;

    // Speed chart EMA state (per-torrent, mirrors TransferStatsViewModel pattern)
    private const double EmaAlpha = 0.3;
    private const int MaxHistoryPoints = 120;
    private double _emaDownload;
    private double _emaUpload;

    #region Static Info (set once in constructor)

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _infoHash = "";
    [ObservableProperty] private string _infoHashV2 = "-";
    [ObservableProperty] private string _savePath = "";
    [ObservableProperty] private string _totalSize = "";
    [ObservableProperty] private string _pieces = "";
    [ObservableProperty] private string _createdOn = "-";
    [ObservableProperty] private string _createdBy = "-";
    [ObservableProperty] private string _comment = "-";
    [ObservableProperty] private string _isPrivate = "-";

    #endregion

    #region Live Summary Stats (updated each tick)

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressDisplay = "0%";
    [ObservableProperty] private double _availability;
    [ObservableProperty] private string _availabilityDisplay = "0.00";
    [ObservableProperty] private string _timeActive = "-";
    [ObservableProperty] private string _eta = "-";
    [ObservableProperty] private string _connections = "-";
    [ObservableProperty] private string _downloaded = "-";
    [ObservableProperty] private string _uploaded = "-";
    [ObservableProperty] private string _seedsDisplay = "-";
    [ObservableProperty] private string _peersDisplay = "-";
    [ObservableProperty] private string _dlSpeed = "-";
    [ObservableProperty] private string _ulSpeed = "-";
    [ObservableProperty] private string _dlLimit = "None";
    [ObservableProperty] private string _ulLimit = "None";
    [ObservableProperty] private string _wasted = "-";
    [ObservableProperty] private string _shareRatio = "0.00";
    [ObservableProperty] private string _reannounceIn = "-";
    [ObservableProperty] private string _lastComplete = "-";
    [ObservableProperty] private string _popularity = "-";
    [ObservableProperty] private bool _isEngineActive;
    [ObservableProperty] private TorrentDisplayState _state;

    #endregion

    #region Tab State

    [ObservableProperty] private int _selectedTabIndex;

    [RelayCommand]
    private void SelectTab(string? tabIndexString)
    {
        if (int.TryParse(tabIndexString, out int tabIndex))
            SelectedTabIndex = tabIndex;
    }

    // Speed chart toggles
    [ObservableProperty] private bool _showDownloadSpeed = true;
    [ObservableProperty] private bool _showUploadSpeed = true;
    [ObservableProperty] private bool _isDarkTheme = true;

    #endregion

    #region Tab Collections

    public ObservableCollection<TrackerEntry> Trackers { get; } = new();
    public ObservableCollection<PeerEntry> Peers { get; } = new();
    public ObservableCollection<WebSeedEntry> WebSeeds { get; } = new();
    public ObservableCollection<FileEntry> Files { get; } = new();
    public ObservableCollection<SpeedDataPoint> DownloadSpeedHistory { get; } = new();
    public ObservableCollection<SpeedDataPoint> UploadSpeedHistory { get; } = new();

    #endregion

    public TorrentDetailsViewModel(string infoHash, ITorrentManagerService service)
    {
        _torrentInfoHash = infoHash ?? throw new ArgumentNullException(nameof(infoHash));
        _service = service ?? throw new ArgumentNullException(nameof(service));

        // Detect current theme
        var app = Avalonia.Application.Current;
        if (app != null)
        {
            IsDarkTheme = app.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            app.ActualThemeVariantChanged += (_, _) =>
            {
                IsDarkTheme = app.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            };
        }

        // Get initial snapshot
        var view = _service.Service.GetTorrentDetails(_torrentInfoHash);
        if (view != null)
        {
            InitializeStaticInfo(view);
            UpdateSummaryStats(view);
        }

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += OnTimerTick;
        _refreshTimer.Start();
    }

    private void InitializeStaticInfo(ManagedTorrentView view)
    {
        Name = view.Name ?? "Unknown";
        InfoHash = view.InfoHash ?? "";
        SavePath = view.SavePath ?? "";

        TotalSize = FormatBytes(view.TotalSize);

        var totalPieces = view.TotalPieces;
        var pieceLength = view.PieceSize;
        var havePieces = view.PiecesCompleted;
        Pieces = totalPieces > 0
            ? $"{totalPieces} x {FormatBytes(pieceLength)} (have {havePieces})"
            : "-";

        // Metadata fields - null-safe for magnet links
        if (view.HasMetadata)
        {
            CreatedOn = view.CreationDate?.ToString("yyyy-MM-dd HH:mm") ?? "-";
            CreatedBy = !string.IsNullOrEmpty(view.Creator) ? view.Creator : "-";
            Comment = !string.IsNullOrEmpty(view.Comment) ? view.Comment : "-";
            IsPrivate = view.IsPrivate ? "Yes" : "No";
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_disposed) return;

        var view = _service.Service.GetTorrentDetails(_torrentInfoHash);
        if (view == null) return;

        UpdateSummaryStats(view);
        AppendSpeedDataPoint(view);

        IsEngineActive = view.IsEngineRunning;

        if (!view.IsEngineRunning)
        {
            Trackers.Clear();
            Peers.Clear();
            WebSeeds.Clear();
            return;
        }

        try
        {
            switch (SelectedTabIndex)
            {
                case 0: UpdateTrackers(view); break;
                case 1: UpdatePeers(view); break;
                case 2: UpdateWebSeeds(view); break;
                case 3: UpdateFiles(view); break;
                // case 4: Speed - updated via AppendSpeedDataPoint above
            }
        }
        catch (ObjectDisposedException) { /* Engine disposed mid-tick, safe to ignore */ }
        catch (InvalidOperationException) { /* Collection modified during snapshot, retry next tick */ }
    }

    #region Summary Stats Update

    private void UpdateSummaryStats(ManagedTorrentView view)
    {
        State = DisplayStateDeriver.Derive(
            view.Status,
            (int)view.PayloadDownloadRate,
            (int)view.PayloadUploadRate,
            view.ConnectedPeers);

        Progress = view.Progress;
        ProgressDisplay = $"{view.Progress * 100:F1}%";
        Availability = view.Availability;
        AvailabilityDisplay = $"{view.Availability:F2}";

        // Transfer group
        TimeActive = FormatDuration(view.ActiveDuration);
        if (view.SeedingDuration > TimeSpan.Zero)
            TimeActive += $" (seeded for {FormatDuration(view.SeedingDuration)})";

        var remaining = view.BytesRemaining;
        var dlRate = view.SmoothedPayloadDownloadRate;
        Eta = remaining > 0 && dlRate > 0
            ? FormatDuration(TimeSpan.FromSeconds(remaining / dlRate))
            : "-";

        Downloaded = FormatBytes(view.AllTimeDownloaded);
        Uploaded = FormatBytes(view.AllTimeUploaded);
        DlSpeed = $"{FormatBytes((long)view.PayloadDownloadRate)}/s";
        UlSpeed = $"{FormatBytes((long)view.PayloadUploadRate)}/s";

        SeedsDisplay = $"{view.ConnectedSeeds} ({view.TrackerSeeders})";
        PeersDisplay = $"{view.ConnectedPeers} ({view.TrackerLeechers})";

        Wasted = FormatBytes(view.TotalWastedBytes);
        ShareRatio = $"{view.StatsRatio:F2}";

        // Max connections from engine
        var maxConn = view.MaxConnections;
        Connections = maxConn > 0
            ? $"{view.ConnectedPeers} ({maxConn} max)"
            : $"{view.ConnectedPeers}";

        // Per-torrent bandwidth limits
        DlLimit = view.IsDownloadLimited
            ? $"{FormatBytes(view.DownloadBandwidthLimit)}/s"
            : "None";
        UlLimit = view.IsUploadLimited
            ? $"{FormatBytes(view.UploadBandwidthLimit)}/s"
            : "None";

        // Update pieces have count
        Pieces = view.TotalPieces > 0
            ? $"{view.TotalPieces} x {FormatBytes(view.PieceSize)} (have {view.PiecesCompleted})"
            : "-";

        if (view.ReannounceIn.HasValue)
            ReannounceIn = FormatDuration(view.ReannounceIn.Value);
        else
            ReannounceIn = "-";

        if (view.LastSeenComplete.HasValue)
        {
            var ago = DateTime.UtcNow - view.LastSeenComplete.Value;
            LastComplete = ago.TotalMinutes < 1 ? "just now" : $"{FormatDuration(ago)} ago";
        }
        else
            LastComplete = "-";

        // Popularity = ratio / max(active_months, 1)
        var activeMonths = Math.Max(view.ActiveDuration.TotalDays / 30.0, 1.0);
        Popularity = $"{view.StatsRatio / activeMonths:F2}";
    }

    #endregion

    #region Tab Update Methods

    private void UpdateTrackers(ManagedTorrentView view)
    {
        var snapshot = view.Trackers;
        if (snapshot.Count == 0 && Trackers.Count == 0) return;

        // Remove stale entries
        for (int i = Trackers.Count - 1; i >= 0; i--)
        {
            if (!snapshot.Any(s => s.Url == Trackers[i].Key))
                Trackers.RemoveAt(i);
        }

        foreach (var ts in snapshot)
        {
            var existing = Trackers.FirstOrDefault(t => t.Key == ts.Url);
            if (existing != null)
            {
                existing.Tier = ts.Tier;
                existing.Status = ts.Status;
                existing.Peers = ts.Peers;
                existing.Seeds = ts.Seeds;
                existing.Leeches = ts.Leeches;
                existing.Message = "";
                existing.ResponseTime = ts.ResponseTime;
            }
            else
            {
                Trackers.Add(new TrackerEntry
                {
                    Tier = ts.Tier,
                    Url = ts.Url,
                    Status = ts.Status,
                    Peers = ts.Peers,
                    Seeds = ts.Seeds,
                    Leeches = ts.Leeches,
                    Message = "",
                    ResponseTime = ts.ResponseTime,
                });
            }
        }
    }

    private void UpdatePeers(ManagedTorrentView view)
    {
        var snapshot = view.Peers;
        if (snapshot.Count == 0 && Peers.Count == 0) return;

        // Remove disconnected peers
        for (int i = Peers.Count - 1; i >= 0; i--)
        {
            var key = Peers[i].Key;
            if (!snapshot.Any(p => $"{p.IpAddress}:{p.Port}" == key))
                Peers.RemoveAt(i);
        }

        foreach (var peer in snapshot)
        {
            var key = $"{peer.IpAddress}:{peer.Port}";

            var existing = Peers.FirstOrDefault(p => p.Key == key);
            if (existing != null)
            {
                existing.Client = peer.Client;
                existing.Progress = peer.Progress;
                existing.DlSpeed = peer.DownloadRateFormatted;
                existing.UlSpeed = peer.UploadRateFormatted;
                existing.Downloaded = FormatBytes(peer.Downloaded);
                existing.Uploaded = FormatBytes(peer.Uploaded);
                existing.Flags = peer.Flags;
                existing.Rtt = $"{peer.RoundTripTimeMs:F0} ms";
            }
            else
            {
                Peers.Add(new PeerEntry
                {
                    Ip = peer.IpAddress,
                    Port = peer.Port,
                    Client = peer.Client,
                    Progress = peer.Progress,
                    DlSpeed = peer.DownloadRateFormatted,
                    UlSpeed = peer.UploadRateFormatted,
                    Downloaded = FormatBytes(peer.Downloaded),
                    Uploaded = FormatBytes(peer.Uploaded),
                    Flags = peer.Flags,
                    Rtt = $"{peer.RoundTripTimeMs:F0} ms",
                });
            }
        }
    }

    private void UpdateFiles(ManagedTorrentView view)
    {
        var files = view.Files;
        if (files.Count == 0) return;

        // Files list is stable (doesn't change during torrent lifetime)
        // Initialize on first call, then update in-place
        if (Files.Count == 0)
        {
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                Files.Add(new FileEntry
                {
                    FileIndex = f.Index,
                    Name = f.Name,
                    Size = FormatBytes(f.Size),
                    Progress = f.Progress,
                    Priority = FormatPriority(f.Priority),
                    Availability = $"{f.Availability:F2}"
                });
            }
        }
        else
        {
            for (int i = 0; i < Math.Min(Files.Count, files.Count); i++)
            {
                var f = files[i];
                var entry = Files[i];
                entry.Progress = f.Progress;
                entry.Priority = FormatPriority(f.Priority);
                entry.Availability = $"{f.Availability:F2}";
            }
        }
    }

    private static string FormatPriority(int priority) => priority switch
    {
        0 => "Do Not Download",
        >= 4 and <= 6 => "High",
        7 => "Maximum",
        _ => "Normal"
    };

    #endregion

    #region Speed Chart

    private void AppendSpeedDataPoint(ManagedTorrentView view)
    {
        var dlRate = (long)view.PayloadDownloadRate;
        var ulRate = (long)view.PayloadUploadRate;

        AddSmoothedPoint(DownloadSpeedHistory, dlRate, ref _emaDownload);
        AddSmoothedPoint(UploadSpeedHistory, ulRate, ref _emaUpload);
    }

    private void AddSmoothedPoint(ObservableCollection<SpeedDataPoint> history, long rawSpeed, ref double ema)
    {
        if (ema == 0 && rawSpeed > 0)
            ema = rawSpeed;
        else if (rawSpeed > 0)
            ema = EmaAlpha * rawSpeed + (1 - EmaAlpha) * ema;
        else
        {
            ema *= 0.5;
            if (ema < 100) ema = 0;
        }

        history.Add(new SpeedDataPoint
        {
            Timestamp = DateTime.Now,
            Speed = (long)ema,
            RawSpeed = rawSpeed
        });

        while (history.Count > MaxHistoryPoints)
            history.RemoveAt(0);
    }

    #endregion

    #region HTTP Sources Update

    private void UpdateWebSeeds(ManagedTorrentView view)
    {
        var seeds = view.WebSeeds;
        if (seeds.Count == 0)
        {
            if (WebSeeds.Count > 0) WebSeeds.Clear();
            return;
        }

        // Resize collection to match
        while (WebSeeds.Count > seeds.Count)
            WebSeeds.RemoveAt(WebSeeds.Count - 1);
        while (WebSeeds.Count < seeds.Count)
            WebSeeds.Add(new WebSeedEntry());

        for (int i = 0; i < seeds.Count; i++)
        {
            var seed = seeds[i];
            var entry = WebSeeds[i];
            entry.Url = seed.Url;
            entry.Type = seed.Type;
            entry.Status = seed.Status;
            entry.Downloaded = FormatBytes(seed.Downloaded);
            entry.DlSpeed = seed.DownloadRateFormatted;
        }
    }

    #endregion

    #region Formatting Helpers

    private static string FormatBytes(long bytes) => FormatHelper.FormatBytesPrecise(bytes);

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnTimerTick;
        Trackers.Clear();
        Peers.Clear();
        WebSeeds.Clear();
        Files.Clear();
    }
}
