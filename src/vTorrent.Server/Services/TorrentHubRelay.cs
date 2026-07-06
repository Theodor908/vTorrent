using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Models;
using vTorrent.Server.Hubs;

namespace vTorrent.Server.Services;

public class TorrentHubRelay : BackgroundService
{
    private readonly ITorrentService _torrentService;
    private readonly IHubContext<TorrentHub> _hubContext;
    private readonly ILogger<TorrentHubRelay> _logger;

    private Dictionary<string, TorrentSnapshotKey> _lastBroadcast = new();

    // Track which torrents have active subscribers to avoid unnecessary GetTorrentDetails calls
    private readonly ConcurrentDictionary<string, int> _subscriptionCounts = new();

    /// <summary>Called by TorrentHub when a client subscribes to a torrent.</summary>
    public void TrackSubscription(string infoHash)
        => _subscriptionCounts.AddOrUpdate(infoHash, 1, (_, count) => count + 1);

    /// <summary>Called by TorrentHub when a client unsubscribes from a torrent.</summary>
    public void UntrackSubscription(string infoHash)
        => _subscriptionCounts.AddOrUpdate(infoHash, 0, (_, count) => Math.Max(0, count - 1));

    public TorrentHubRelay(
        ITorrentService torrentService,
        IHubContext<TorrentHub> hubContext,
        ILogger<TorrentHubRelay> logger)
    {
        _torrentService = torrentService;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe to immediate events
        _torrentService.TorrentAdded += OnTorrentAdded;
        _torrentService.TorrentRemoved += OnTorrentRemoved;
        _torrentService.TorrentCompleted += OnTorrentCompleted;
        _torrentService.TorrentStatusChanged += OnTorrentStatusChanged;
        _torrentService.TorrentError += OnTorrentError;
        _torrentService.DhtStateChanged += OnDhtStateChanged;
        _torrentService.CategoryChanged += OnCategoryChanged;
        _torrentService.TagChanged += OnTagChanged;
        _torrentService.ProfileChanged += OnProfileChanged;
        _torrentService.ScheduleToggled += OnScheduleToggled;

        _logger.LogInformation("TorrentHubRelay started");

        // Periodic batched updates
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1500));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await BroadcastChangedTorrentsAsync();
                await BroadcastStatsAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _torrentService.TorrentAdded -= OnTorrentAdded;
            _torrentService.TorrentRemoved -= OnTorrentRemoved;
            _torrentService.TorrentCompleted -= OnTorrentCompleted;
            _torrentService.TorrentStatusChanged -= OnTorrentStatusChanged;
            _torrentService.TorrentError -= OnTorrentError;
            _torrentService.DhtStateChanged -= OnDhtStateChanged;
            _torrentService.CategoryChanged -= OnCategoryChanged;
            _torrentService.TagChanged -= OnTagChanged;
            _torrentService.ProfileChanged -= OnProfileChanged;
            _torrentService.ScheduleToggled -= OnScheduleToggled;
        }
    }

    // --- Immediate events ---

    private async void OnTorrentAdded(object? sender, string infoHash)
    {
        try
        {
            var snapshot = _torrentService.GetTorrent(infoHash);
            if (snapshot != null)
                await _hubContext.Clients.All.SendAsync("TorrentAdded", snapshot);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error broadcasting TorrentAdded"); }
    }

    private async void OnTorrentRemoved(object? sender, string infoHash)
    {
        try { await _hubContext.Clients.All.SendAsync("TorrentRemoved", infoHash); }
        catch (Exception ex) { _logger.LogError(ex, "Error broadcasting TorrentRemoved"); }
    }

    private async void OnTorrentCompleted(object? sender, string infoHash)
    {
        try { await _hubContext.Clients.All.SendAsync("TorrentCompleted", infoHash); }
        catch (Exception ex) { _logger.LogError(ex, "Error broadcasting TorrentCompleted"); }
    }

    private async void OnTorrentStatusChanged(object? sender, Abstractions.Events.TorrentStatusChangedEventArgs e)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("TorrentStatusChanged", new
            {
                e.InfoHash,
                OldStatus = new
                {
                    Phase = e.OldStatus.Phase.ToString(),
                    Intent = e.OldStatus.Intent.ToString(),
                    HasError = e.OldStatus.Error.HasValue,
                    MissingFiles = e.OldStatus.MissingFiles,
                    FileOp = e.OldStatus.FileOp.ToString(),
                },
                NewStatus = new
                {
                    Phase = e.NewStatus.Phase.ToString(),
                    Intent = e.NewStatus.Intent.ToString(),
                    HasError = e.NewStatus.Error.HasValue,
                    MissingFiles = e.NewStatus.MissingFiles,
                    FileOp = e.NewStatus.FileOp.ToString(),
                },
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error broadcasting TorrentStatusChanged"); }
    }

    private async void OnTorrentError(object? sender, Abstractions.Events.TorrentErrorEventArgs e)
    {
        try { await _hubContext.Clients.All.SendAsync("TorrentError", new { e.InfoHash, e.ErrorMessage }); }
        catch (Exception ex) { _logger.LogError(ex, "Error broadcasting TorrentError"); }
    }

    private async void OnDhtStateChanged(object? sender, Abstractions.Events.DhtStateChangedEventArgs e)
    {
        try { await _hubContext.Clients.All.SendAsync("DhtStateChanged", new { e.IsRunning, e.NodeCount }); }
        catch (Exception ex) { _logger.LogError(ex, "Error broadcasting DhtStateChanged"); }
    }

    private async void OnCategoryChanged(object? sender, int categoryId)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("CategoriesChanged");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting CategoriesChanged");
        }
    }

    private async void OnTagChanged(object? sender, int tagId)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("TagsChanged");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting TagsChanged");
        }
    }

    private async void OnProfileChanged(object? sender, string profileName)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("ProfileChanged");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting ProfileChanged");
        }
    }

    private async void OnScheduleToggled(object? sender, bool enabled)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("ScheduleToggled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting ScheduleToggled");
        }
    }

    // --- Batched updates ---

    private async Task BroadcastChangedTorrentsAsync()
    {
        var current = _torrentService.GetTorrents();
        var changed = new List<TorrentSnapshot>();

        var newKeys = new Dictionary<string, TorrentSnapshotKey>();

        foreach (var s in current)
        {
            var key = new TorrentSnapshotKey(s.PayloadDownloadRate, s.PayloadUploadRate,
                s.VerifiedProgress, s.ConnectedPeers, s.ConnectedSeeds,
                s.Status.Phase, s.Status.Intent, s.Status.Error.HasValue, s.Status.MissingFiles);
            newKeys[s.InfoHash] = key;

            if (!_lastBroadcast.TryGetValue(s.InfoHash, out var prev) || prev != key)
                changed.Add(s);
        }

        _lastBroadcast = newKeys;

        if (changed.Count > 0)
            await _hubContext.Clients.All.SendAsync("TorrentsUpdated", changed);

        // Per-torrent detail updates — only for torrents with active subscribers
        foreach (var s in changed)
        {
            if (_subscriptionCounts.TryGetValue(s.InfoHash, out var count) && count > 0)
            {
                var details = _torrentService.GetTorrentDetails(s.InfoHash);
                if (details != null)
                    await _hubContext.Clients.Group($"torrent:{s.InfoHash}").SendAsync("TorrentDetailUpdated", details);
            }
        }
    }

    private async Task BroadcastStatsAsync()
    {
        await _hubContext.Clients.All.SendAsync("StatsUpdated", _torrentService.SessionStats);
    }

    // Lightweight comparison key — cheap value-type equality
    private record struct TorrentSnapshotKey(
        int DownloadRate, int UploadRate, double Progress,
        int Peers, int Seeds,
        Abstractions.Enums.TransferPhase Phase,
        Abstractions.Enums.UserIntent Intent,
        bool HasError, bool MissingFiles);
}
