using vTorrent.Desktop.Formatting;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using vTorrent.Abstractions.Models;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels;

/// <summary>
/// ViewModel for the transfer statistics panel.
/// Displays download/upload speeds, session stats, and maintains speed history for charting.
/// Follows Single Responsibility - only transfer statistics concerns.
/// </summary>
public partial class TransferStatsViewModel : BaseViewModel
{
    private const int MaxHistoryPoints = 70; // Extra buffer so oldest points are deleted off-screen
    private const double EmaAlpha = 0.3; // EMA smoothing factor (0.1-0.5, lower = smoother)

    private readonly ITorrentManagerService? _torrentManager;

    // EMA state for smoothing raw speed values
    private double _emaDownload = 0;
    private double _emaUpload = 0;

    #region Speed Properties

    [ObservableProperty]
    private long _downloadSpeed;

    [ObservableProperty]
    private long _uploadSpeed;

    [ObservableProperty]
    private string _downloadSpeedFormatted = "0 B/s";

    [ObservableProperty]
    private string _uploadSpeedFormatted = "0 B/s";

    #endregion

    #region Session Statistics

    [ObservableProperty]
    private int _seeds;

    [ObservableProperty]
    private double _ratio = 1.0;

    [ObservableProperty]
    private long _totalDownloaded;

    [ObservableProperty]
    private long _totalUploaded;

    [ObservableProperty]
    private string _totalDownloadedFormatted = "0 B";

    [ObservableProperty]
    private string _totalUploadedFormatted = "0 B";

    [ObservableProperty]
    private TimeSpan _timeElapsed;

    [ObservableProperty]
    private TimeSpan _timeRemaining;

    [ObservableProperty]
    private string _timeElapsedFormatted = "0 sec";

    [ObservableProperty]
    private string _timeRemainingFormatted = "0 sec";

    #endregion

    #region Chart Data

    [ObservableProperty]
    private ObservableCollection<SpeedDataPoint> _downloadHistory = new();

    [ObservableProperty]
    private ObservableCollection<SpeedDataPoint> _uploadHistory = new();

    [ObservableProperty]
    private bool _showDownloadLine = true;

    [ObservableProperty]
    private bool _showUploadLine = true;

    [ObservableProperty]
    private bool _isDarkTheme = true;

    #endregion

    public TransferStatsViewModel() : this(null)
    {
    }

    public TransferStatsViewModel(ITorrentManagerService? torrentManager)
    {
        _torrentManager = torrentManager;

        if (_torrentManager != null)
        {
            // Wire up stats updates
            _torrentManager.StatsUpdated += OnStatsUpdated;
        }
        else
        {
            // Initialize with sample data for design-time
            InitializeSampleData();
        }
    }

    #region Property Changed Handlers

    partial void OnDownloadSpeedChanged(long value)
    {
        DownloadSpeedFormatted = FormatSpeed(value);
        // Note: Chart data points are added in UpdateFromSession() to ensure
        // continuous updates even when values don't change
    }

    partial void OnUploadSpeedChanged(long value)
    {
        UploadSpeedFormatted = FormatSpeed(value);
        // Note: Chart data points are added in UpdateFromSession() to ensure
        // continuous updates even when values don't change
    }

    partial void OnTotalDownloadedChanged(long value)
    {
        TotalDownloadedFormatted = FormatBytes(value);
        UpdateRatio();
    }

    partial void OnTotalUploadedChanged(long value)
    {
        TotalUploadedFormatted = FormatBytes(value);
        UpdateRatio();
    }

    partial void OnTimeElapsedChanged(TimeSpan value)
    {
        TimeElapsedFormatted = FormatTimeSpan(value);
    }

    partial void OnTimeRemainingChanged(TimeSpan value)
    {
        TimeRemainingFormatted = FormatTimeSpan(value);
    }

    #endregion

    #region Service Integration

    private void OnStatsUpdated(object? sender, StatsUpdatedEventArgs e)
    {
        UpdateFromGrid(e.Torrents);
        UpdateSessionInfo(e.Statistics);
    }

    /// <summary>
    /// Aggregate all transfer stats directly from the torrent grid.
    /// This guarantees grid rows add up to global values — no discrepancy possible.
    /// </summary>
    public void UpdateFromGrid(IReadOnlyList<Desktop.ViewModels.TorrentViewModel> torrents)
    {
        var downloadSpeed = torrents.Sum(t => (long)t.DownloadRate);
        var uploadSpeed = torrents.Sum(t => (long)t.UploadRate);

        // Chart data (continuous, every tick)
        AddSpeedDataPoint(DownloadHistory, downloadSpeed, isDownload: true);
        AddSpeedDataPoint(UploadHistory, uploadSpeed, isDownload: false);

        DownloadSpeed = downloadSpeed;
        UploadSpeed = uploadSpeed;
        Seeds = torrents.Sum(t => t.ConnectedSeeds);
        TotalDownloaded = torrents.Sum(t => t.TotalDone);
        TotalUploaded = torrents.Sum(t => t.Uploaded);

        // Global ETA = max of all downloading torrents' ETAs
        var downloading = torrents
            .Where(t => t.State is Desktop.ViewModels.TorrentDisplayState.Downloading or Desktop.ViewModels.TorrentDisplayState.ForcedDownloading && t.ETA.HasValue)
            .ToList();

        if (downloading.Count > 0)
        {
            TimeRemaining = downloading.Max(t => t.ETA!.Value);
        }
        else if (downloadSpeed == 0)
        {
            TimeRemaining = TimeSpan.Zero;
        }
        // Otherwise keep last valid TimeRemaining (smooths brief gaps)
    }

    /// <summary>
    /// Update non-aggregate session info (uptime, etc.)
    /// </summary>
    public void UpdateSessionInfo(SessionStatistics stats)
    {
        TimeElapsed = stats.Uptime;
    }

    /// <summary>
    /// Add a speed sample for chart display
    /// </summary>
    public void AddSpeedSample(long download, long upload)
    {
        // Add chart data points
        AddSpeedDataPoint(DownloadHistory, download, isDownload: true);
        AddSpeedDataPoint(UploadHistory, upload, isDownload: false);

        DownloadSpeed = download;
        UploadSpeed = upload;
    }

    #endregion

    #region Private Methods

    private void UpdateRatio()
    {
        Ratio = TotalDownloaded > 0 ? (double)TotalUploaded / TotalDownloaded : 0;
    }

    private void AddSpeedDataPoint(ObservableCollection<SpeedDataPoint> history, long speed, bool isDownload)
    {
        // Apply EMA smoothing to reduce jitter
        // LIBTORRENT-STYLE: When speed is 0, decay much faster (0.5 instead of 0.9)
        // This ensures graphs collapse quickly when paused (libtorrent uses ~80% decay per second)
        long smoothedSpeed;

        if (isDownload)
        {
            if (_emaDownload == 0 && speed > 0)
            {
                // First non-zero value - initialize directly without blending
                _emaDownload = speed;
                smoothedSpeed = speed;
            }
            else if (speed > 0)
            {
                // Apply EMA: newValue = alpha * rawValue + (1 - alpha) * previousEMA
                _emaDownload = EmaAlpha * speed + (1 - EmaAlpha) * _emaDownload;
                smoothedSpeed = (long)_emaDownload;
            }
            else
            {
                // LIBTORRENT-STYLE FAST DECAY: Use 0.5 multiplier for quick graph collapse
                // After 1 tick: 50%, 2 ticks: 25%, 3 ticks: 12.5%, 4 ticks: ~6%
                // Graph reaches near-zero in ~4-5 seconds (matches libtorrent behavior)
                _emaDownload *= 0.5;
                // Floor at small value to reach true 0
                if (_emaDownload < 100) _emaDownload = 0;
                smoothedSpeed = (long)_emaDownload;
            }
        }
        else
        {
            if (_emaUpload == 0 && speed > 0)
            {
                // First non-zero value - initialize directly without blending
                _emaUpload = speed;
                smoothedSpeed = speed;
            }
            else if (speed > 0)
            {
                // Apply EMA: newValue = alpha * rawValue + (1 - alpha) * previousEMA
                _emaUpload = EmaAlpha * speed + (1 - EmaAlpha) * _emaUpload;
                smoothedSpeed = (long)_emaUpload;
            }
            else
            {
                // LIBTORRENT-STYLE FAST DECAY: Use 0.5 multiplier for quick graph collapse
                _emaUpload *= 0.5;
                if (_emaUpload < 100) _emaUpload = 0;
                smoothedSpeed = (long)_emaUpload;
            }
        }

        history.Add(new SpeedDataPoint
        {
            Timestamp = DateTime.Now,
            Speed = smoothedSpeed,
            RawSpeed = speed
        });

        // Keep only recent history
        while (history.Count > MaxHistoryPoints)
        {
            history.RemoveAt(0);
        }
    }

    private void InitializeSampleData()
    {
        // Initialize with sample data matching the screenshot
        DownloadSpeed = 14155776; // ~13.5 MB/s
        UploadSpeed = 10695475;   // ~10.2 MB/s
        Seeds = 12;
        Ratio = 1.2;
        TotalDownloaded = 722468864;  // 689 MB
        TotalUploaded = 160432128;    // 153 MB
        TimeElapsed = TimeSpan.FromSeconds(513); // 8 min 33 sec
        TimeRemaining = TimeSpan.FromSeconds(80); // 1 min 20 sec

        // Generate sample chart data
        var now = DateTime.Now;
        var random = new Random(42); // Fixed seed for consistency

        for (int i = MaxHistoryPoints - 1; i >= 0; i--)
        {
            var timestamp = now.AddSeconds(-i);

            // Create a smooth curve with some variation
            var baseDownload = 14000000L + (long)(Math.Sin(i * 0.1) * 2000000);
            var baseUpload = 10000000L + (long)(Math.Sin(i * 0.15) * 1500000);

            var downloadRaw = baseDownload + random.Next(-500000, 500000);
            var uploadRaw = baseUpload + random.Next(-300000, 300000);

            DownloadHistory.Add(new SpeedDataPoint
            {
                Timestamp = timestamp,
                Speed = downloadRaw,
                RawSpeed = downloadRaw
            });

            UploadHistory.Add(new SpeedDataPoint
            {
                Timestamp = timestamp,
                Speed = uploadRaw,
                RawSpeed = uploadRaw
            });
        }
    }

    #endregion

    #region Formatting Helpers

    private static string FormatBytes(long bytes) => FormatHelper.FormatBytes(bytes);
    private static string FormatSpeed(long bytesPerSecond) => FormatHelper.FormatSpeed(bytesPerSecond);
    private static string FormatTimeSpan(TimeSpan time) => FormatHelper.FormatTimeSpan(time);

    #endregion
}

/// <summary>
/// Data point for speed chart with smoothing support
/// </summary>
public class SpeedDataPoint
{
    public DateTime Timestamp { get; set; }
    public long Speed { get; set; }  // Smoothed speed (EMA filtered)
    public long RawSpeed { get; set; }  // Original unsmoothed speed
}
