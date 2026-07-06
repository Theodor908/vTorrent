using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using vTorrent.Bench.Bench;
using vTorrent.Bench.Settings;

namespace vTorrent.Bench.Dashboard;

public sealed class DashboardState
{
    public double DownloadRate { get; init; }
    public double UploadRate { get; init; }
    public double PayloadRatio { get; init; }
    public int PiecesCompleted { get; init; }
    public int TotalPieces { get; init; }
    public int HashFailures { get; init; }
    public int ActiveConnections { get; init; }
    public int TotalPeers { get; init; }
    public int UnchokedCount { get; init; }
    public int ChokedCount { get; init; }
    public int SnubbedCount { get; init; }
    public int PendingRequests { get; init; }
    public double AvgQueueDepth { get; init; }
    public bool IsEndgame { get; init; }
    public bool IsPaused { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public sealed class DashboardRenderer
{
    private readonly SettingsRegistry _registry;
    private readonly List<Snapshot> _snapshots;
    private readonly List<string> _changeLog;
    private readonly string _scenarioName;

    private readonly List<string> _groups;
    private int _selectedGroup;
    private int _selectedSetting;

    private readonly Sparkline _downloadSparkline = new(60);
    private readonly Sparkline _uploadSparkline = new(60);

    public int SelectedGroup
    {
        get => _selectedGroup;
        set => _selectedGroup = _groups.Count == 0 ? 0 : Math.Clamp(value, 0, _groups.Count - 1);
    }

    public int SelectedSetting
    {
        get => _selectedSetting;
        set
        {
            var group = CurrentGroupName;
            var count = group != null ? _registry.GetGroup(group).Count : 0;
            _selectedSetting = count == 0 ? 0 : Math.Clamp(value, 0, count - 1);
        }
    }

    private string? CurrentGroupName => _groups.Count == 0 ? null : _groups[_selectedGroup];

    public DashboardRenderer(SettingsRegistry registry, List<Snapshot> snapshots, List<string> changeLog, string scenarioName)
    {
        _registry = registry;
        _snapshots = snapshots;
        _changeLog = changeLog;
        _scenarioName = scenarioName;
        _groups = registry.Groups().ToList();
    }

    public SettingDefinition? GetSelectedDefinition()
    {
        var groupName = CurrentGroupName;
        if (groupName == null) return null;
        var defs = _registry.GetGroup(groupName);
        if (defs.Count == 0) return null;
        return defs[Math.Clamp(_selectedSetting, 0, defs.Count - 1)];
    }

    public IRenderable Render(DashboardState state)
    {
        _downloadSparkline.Add(state.DownloadRate);
        _uploadSparkline.Add(state.UploadRate);

        var left = BuildLeftPanel(state);
        var right = BuildRightPanel(state);

        var grid = new Grid();
        grid.AddColumn(new GridColumn().Width(46));
        grid.AddColumn(new GridColumn());
        grid.AddRow(left, right);

        return grid;
    }

    public string RenderStatusBar(DashboardState state)
    {
        var elapsed = state.Elapsed;
        var elapsedStr = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
        var pausedStr = state.IsPaused ? " [yellow]PAUSED[/]" : "";
        var endgameStr = state.IsEndgame ? " [bold cyan]ENDGAME[/]" : "";
        var scenario = Markup.Escape(_scenarioName);
        var hashStr = state.HashFailures > 0 ? $" [red]hash-fail:{state.HashFailures}[/]" : "";
        return $"[bold]vTorrent Bench[/] | scenario:[cyan]{scenario}[/] | elapsed:[green]{elapsedStr}[/]{pausedStr}{endgameStr}{hashStr}";
    }

    public string RenderHotkeyBar()
    {
        return "[grey][[↑↓]][/] setting  [grey][[←→]][/] group  [grey][[+/-]][/] adjust  [grey][[S]][/] snapshot  [grey][[R]][/] reset  [grey][[Q]][/] quit  [grey][[E]][/] export";
    }

    private IRenderable BuildLeftPanel(DashboardState state)
    {
        var rows = new List<IRenderable>();

        // Transfer section
        rows.Add(new Rule("[bold yellow]Transfer[/]") { Justification = Justify.Left });
        var dlSpeed = FormatSpeed(state.DownloadRate);
        var ulSpeed = FormatSpeed(state.UploadRate);
        var payloadRatio = $"{state.PayloadRatio:F1}%";
        rows.Add(new Markup($"  [green]DN[/] {Markup.Escape(dlSpeed),12}  [blue]UP[/] {Markup.Escape(ulSpeed),12}  [grey]payload[/] {Markup.Escape(payloadRatio)}"));
        rows.Add(new Markup($"  [green]{Markup.Escape(_downloadSparkline.Render(40))}[/]"));
        rows.Add(new Markup($"  [blue]{Markup.Escape(_uploadSparkline.Render(40))}[/]"));

        // Pieces section
        rows.Add(new Rule("[bold yellow]Pieces[/]") { Justification = Justify.Left });
        var pct = state.TotalPieces == 0 ? 0.0 : (double)state.PiecesCompleted / state.TotalPieces;
        var barFilled = (int)(pct * 38);
        var bar = "[" + new string('#', barFilled) + new string('-', 38 - barFilled) + "]";
        rows.Add(new Markup($"  [white]{state.PiecesCompleted}[/] / [grey]{state.TotalPieces}[/]  {Markup.Escape($"{pct * 100:F1}%")}"));
        rows.Add(new Markup($"  [green]{Markup.Escape(bar)}[/]"));
        if (state.HashFailures > 0)
            rows.Add(new Markup($"  [red]hash failures: {state.HashFailures}[/]"));

        // Connections section
        rows.Add(new Rule("[bold yellow]Connections[/]") { Justification = Justify.Left });
        rows.Add(new Markup($"  active:[cyan]{state.ActiveConnections}[/]/{state.TotalPeers}  unchoked:[green]{state.UnchokedCount}[/]  choked:[grey]{state.ChokedCount}[/]  snubbed:[yellow]{state.SnubbedCount}[/]"));

        // Pipeline section
        rows.Add(new Rule("[bold yellow]Pipeline[/]") { Justification = Justify.Left });
        var endgameStr = state.IsEndgame ? "[bold cyan]ENDGAME[/] " : "";
        rows.Add(new Markup($"  {endgameStr}pending:[white]{state.PendingRequests}[/]  avg-depth:[white]{state.AvgQueueDepth:F1}[/]"));

        return new Panel(new Rows(rows))
        {
            Header = new PanelHeader("[bold] Metrics [/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(0, 0, 0, 0),
        };
    }

    private IRenderable BuildRightPanel(DashboardState state)
    {
        var rows = new List<IRenderable>();

        // Settings group section
        var groupName = CurrentGroupName ?? "(none)";
        rows.Add(new Rule($"[bold yellow]Settings — {Markup.Escape(groupName)}[/]") { Justification = Justify.Left });

        if (CurrentGroupName != null)
        {
            var defs = _registry.GetGroup(CurrentGroupName);
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                var selected = i == _selectedSetting;
                var cursor = selected ? "[bold cyan]>[/]" : " ";
                var label = Markup.Escape(def.Label);
                var value = Markup.Escape(def.FormatValue());
                var changed = def.HasChanged() ? $" [grey](was {Markup.Escape(def.FormatInitial())})[/]" : "";
                var valueColor = selected ? "bold white" : "white";
                rows.Add(new Markup($" {cursor} {label,-32} [{valueColor}]{value}[/]{changed}"));
            }

            // Group navigation hint
            var groupNames = _groups;
            var groupNav = new StringBuilder();
            for (int g = 0; g < groupNames.Count; g++)
            {
                var gn = groupNames[g];
                if (g == _selectedGroup)
                    groupNav.Append($"[bold cyan underline]{Markup.Escape(gn)}[/] ");
                else
                    groupNav.Append($"[grey]{Markup.Escape(gn)}[/] ");
            }
            rows.Add(new Markup(""));
            rows.Add(new Markup($"  {groupNav}"));
        }

        // Snapshots section
        rows.Add(new Rule("[bold yellow]Snapshots[/]") { Justification = Justify.Left });
        if (_snapshots.Count == 0)
        {
            rows.Add(new Markup("  [grey](none yet — press S to capture)[/]"));
        }
        else
        {
            var recent = _snapshots.Count <= 5
                ? _snapshots
                : _snapshots.GetRange(_snapshots.Count - 5, 5);

            Snapshot? prev = null;
            foreach (var snap in recent)
            {
                var elapsed = $"{(int)snap.Elapsed.TotalMinutes:D2}:{snap.Elapsed.Seconds:D2}";
                var dlStr = FormatSpeed(snap.Metrics.DownloadRate);
                var label = snap.Label != null ? $" [{Markup.Escape(snap.Label)}]" : "";

                string deltaStr = "";
                if (prev != null && prev.Metrics.DownloadRate > 0)
                {
                    var delta = (snap.Metrics.DownloadRate - prev.Metrics.DownloadRate) / prev.Metrics.DownloadRate * 100;
                    var color = delta >= 0 ? "green" : "red";
                    deltaStr = $" [{color}]{delta:+0.0;-0.0}%[/]";
                }

                rows.Add(new Markup($"  [grey]#{snap.Id}[/] @{Markup.Escape(elapsed)}{Markup.Escape(label)}  [green]{Markup.Escape(dlStr)}[/]{deltaStr}"));
                prev = snap;
            }
        }

        // Change log section
        rows.Add(new Rule("[bold yellow]Change Log[/]") { Justification = Justify.Left });
        if (_changeLog.Count == 0)
        {
            rows.Add(new Markup("  [grey](no changes yet)[/]"));
        }
        else
        {
            var recent = _changeLog.Count <= 6
                ? _changeLog
                : _changeLog.GetRange(_changeLog.Count - 6, 6);
            foreach (var entry in recent)
                rows.Add(new Markup($"  [grey]·[/] {Markup.Escape(entry)}"));
        }

        return new Panel(new Rows(rows))
        {
            Header = new PanelHeader("[bold] Controls [/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(0, 0, 0, 0),
        };
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1_048_576) return $"{bytesPerSec / 1_048_576:F1} MB/s";
        if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024:F0} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }
}
