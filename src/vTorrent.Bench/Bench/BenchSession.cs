using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using vTorrent.Bench.Config;
using vTorrent.Bench.Dashboard;
using vTorrent.Bench.Export;
using vTorrent.Bench.Settings;
using vTorrent.Core.Engine;

namespace vTorrent.Bench.Bench;

public sealed class BenchSession
{
    private readonly ScenarioConfig _config;
    private readonly string _scenarioName;
    private readonly string? _exportCsvPath;

    public BenchSession(ScenarioConfig config, string scenarioName, string? exportCsvPath = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _scenarioName = scenarioName;
        _exportCsvPath = exportCsvPath;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // 1. Create EngineMount from config
        using var mount = new EngineMount(_config);

        // 2. Build SettingsRegistry from mount's monitors
        var registry = mount.BuildSettingsRegistry();

        // 3. Shared state
        var snapshots = new List<Snapshot>();
        var changeLog = new List<string>();
        var timeSeries = new TimeSeriesExporter();
        var paused = false;
        var stopwatch = Stopwatch.StartNew();

        // 4. Create DashboardRenderer
        var dashboard = new DashboardRenderer(registry, snapshots, changeLog, _scenarioName);

        // 5. Wire HotkeyHandler
        var hotkeyHandler = new HotkeyHandler();
        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        hotkeyHandler.GroupSelected += g => dashboard.SelectedGroup = g;
        hotkeyHandler.NavigateUp += () => dashboard.SelectedSetting--;
        hotkeyHandler.NavigateDown += () => dashboard.SelectedSetting++;

        hotkeyHandler.IncreaseValue += () =>
        {
            var def = dashboard.GetSelectedDefinition();
            if (def == null) return;
            var oldVal = def.Getter();
            var newVal = def.Increase();
            var entry = $"{def.Label}: {oldVal} -> {newVal}";
            changeLog.Add(entry);
        };

        hotkeyHandler.DecreaseValue += () =>
        {
            var def = dashboard.GetSelectedDefinition();
            if (def == null) return;
            var oldVal = def.Getter();
            var newVal = def.Decrease();
            var entry = $"{def.Label}: {oldVal} -> {newVal}";
            changeLog.Add(entry);
        };

        hotkeyHandler.TakeSnapshot += () =>
        {
            var metrics = GatherMetrics(mount, stopwatch.Elapsed);
            var snap = Snapshot.Capture(snapshots.Count + 1, stopwatch.Elapsed, metrics, registry);
            snapshots.Add(snap);
            changeLog.Add($"Snapshot #{snap.Id} captured");
        };

        hotkeyHandler.CompareSnapshots += () =>
        {
            if (snapshots.Count < 2) return;
            var a = snapshots[snapshots.Count - 2];
            var b = snapshots[snapshots.Count - 1];
            var rows = SnapshotComparer.Compare(a, b, registry);

            var table = new Table();
            table.AddColumn("Metric");
            table.AddColumn($"#{a.Id}");
            table.AddColumn($"#{b.Id}");
            table.AddColumn("Delta");
            foreach (var row in rows)
                table.AddRow(
                    Markup.Escape(row.Label),
                    Markup.Escape(row.ValueA),
                    Markup.Escape(row.ValueB),
                    Markup.Escape(row.Delta));

            AnsiConsole.Clear();
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine("[grey]Press any key to return...[/]");
            Console.ReadKey(intercept: true);
        };

        hotkeyHandler.ExportProfile += () =>
        {
            var path = $"bench-profile-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            ProfileExporter.Export(registry, _scenarioName, path);
            changeLog.Add($"Profile exported to {path}");
        };

        hotkeyHandler.ResetSettings += () =>
        {
            foreach (var def in registry.All)
            {
                if (def.InitialValue != null)
                    def.Setter(def.InitialValue);
            }
            changeLog.Add("All settings reset to initial values");
        };

        hotkeyHandler.TogglePause += () =>
        {
            paused = !paused;
            changeLog.Add(paused ? "Paused" : "Resumed");
        };

        hotkeyHandler.Quit += () =>
        {
            sessionCts.Cancel();
        };

        // 6. Start hotkey handler
        var hotkeyTask = hotkeyHandler.RunAsync(sessionCts.Token);

        // 7. Start engine mount
        await mount.StartAsync(sessionCts.Token).ConfigureAwait(false);

        // 8. Run Spectre.Console Live render loop
        try
        {
            await AnsiConsole.Live(new Markup("[grey]Starting...[/]"))
                .AutoClear(true)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async ctx =>
                {
                    while (!sessionCts.Token.IsCancellationRequested)
                    {
                        var elapsed = stopwatch.Elapsed;
                        var state = BuildDashboardState(mount, elapsed, paused);

                        // Record time-series sample
                        timeSeries.Record(
                            (long)elapsed.TotalMilliseconds,
                            state.DownloadRate,
                            state.UploadRate,
                            state.PayloadRatio,
                            state.PiecesCompleted,
                            state.ActiveConnections,
                            state.UnchokedCount,
                            state.AvgQueueDepth);

                        // Build renderable
                        var grid = new Grid();
                        grid.AddColumn();
                        grid.AddRow(new Markup(dashboard.RenderStatusBar(state)));
                        grid.AddRow(dashboard.Render(state));
                        grid.AddRow(new Markup(dashboard.RenderHotkeyBar()));

                        ctx.UpdateTarget(grid);

                        try
                        {
                            await Task.Delay(500, sessionCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal exit via Quit
        }

        // 9. Stop engine mount
        await mount.StopAsync().ConfigureAwait(false);

        // 10. Optionally export CSV
        if (_exportCsvPath != null)
        {
            timeSeries.Export(_exportCsvPath);
            AnsiConsole.MarkupLine($"[green]Time-series exported to {Markup.Escape(_exportCsvPath)}[/]");
        }

        // Wait for hotkey task to finish
        try { await hotkeyTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private static DashboardState BuildDashboardState(EngineMount mount, TimeSpan elapsed, bool paused)
    {
        var stats = mount.Statistics;
        IStatisticsTracker tracker = stats;
        var peers = mount.PeerManager.ConnectedPeers;

        int unchokedCount = 0;
        int chokedCount = 0;
        int snubbedCount = 0;
        foreach (var peer in peers)
        {
            if (peer.IsChoked)
                chokedCount++;
            else
                unchokedCount++;
            if (peer.IsSnubbed)
                snubbedCount++;
        }

        var payloadDownloaded = tracker.PayloadDownloaded;
        var totalDownloaded = tracker.TotalDownloaded;
        var payloadRatio = totalDownloaded > 0
            ? (double)payloadDownloaded / totalDownloaded * 100.0
            : 100.0;

        return new DashboardState
        {
            DownloadRate = stats.DownloadRate,
            UploadRate = stats.UploadRate,
            PayloadRatio = payloadRatio,
            PiecesCompleted = stats.PiecesCompleted,
            TotalPieces = mount.SyntheticTorrent.Info.PieceCount,
            HashFailures = (int)stats.DiskHashFailed,
            ActiveConnections = mount.PeerManager.ConnectedPeerCount,
            TotalPeers = mount.PeerManager.ConnectedPeerCount,
            UnchokedCount = unchokedCount,
            ChokedCount = chokedCount,
            SnubbedCount = snubbedCount,
            PendingRequests = mount.DownloadCoordinator.PendingRequests,
            AvgQueueDepth = mount.PeerManager.ConnectedPeerCount > 0
                ? (double)mount.DownloadCoordinator.PendingRequests / mount.PeerManager.ConnectedPeerCount
                : 0,
            IsEndgame = mount.DownloadCoordinator.IsEndgameMode,
            IsPaused = paused,
            Elapsed = elapsed,
        };
    }

    private static SnapshotMetrics GatherMetrics(EngineMount mount, TimeSpan elapsed)
    {
        var stats = mount.Statistics;
        IStatisticsTracker tracker = stats;
        var peers = mount.PeerManager.ConnectedPeers;

        int unchokedCount = 0;
        foreach (var peer in peers)
            if (!peer.IsChoked)
                unchokedCount++;

        var payloadDownloaded = tracker.PayloadDownloaded;
        var totalDownloaded = tracker.TotalDownloaded;
        var payloadRatio = totalDownloaded > 0
            ? (double)payloadDownloaded / totalDownloaded * 100.0
            : 100.0;

        var piecesPerSecond = elapsed.TotalSeconds > 0
            ? stats.PiecesCompleted / elapsed.TotalSeconds
            : 0;

        var avgQueueDepth = mount.PeerManager.ConnectedPeerCount > 0
            ? (double)mount.DownloadCoordinator.PendingRequests / mount.PeerManager.ConnectedPeerCount
            : 0;

        return new SnapshotMetrics(
            DownloadRate: stats.DownloadRate,
            UploadRate: stats.UploadRate,
            PayloadRatio: payloadRatio,
            PiecesCompleted: stats.PiecesCompleted,
            PiecesPerSecond: piecesPerSecond,
            ActiveConnections: mount.PeerManager.ConnectedPeerCount,
            UnchokedCount: unchokedCount,
            AvgQueueDepth: avgQueueDepth,
            HashFailures: (int)stats.DiskHashFailed);
    }
}
