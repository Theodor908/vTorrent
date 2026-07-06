using System;
using System.Collections.Generic;
using System.Linq;
using vTorrent.Bench.Settings;

namespace vTorrent.Bench.Bench;

public sealed record SnapshotMetrics(
    double DownloadRate, double UploadRate, double PayloadRatio,
    int PiecesCompleted, double PiecesPerSecond,
    int ActiveConnections, int UnchokedCount,
    double AvgQueueDepth, int HashFailures);

public sealed class Snapshot
{
    public int Id { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Label { get; init; }
    public SnapshotMetrics Metrics { get; init; } = null!;
    public Dictionary<string, object> SettingsValues { get; init; } = new();

    public static Snapshot Capture(int id, TimeSpan elapsed, SnapshotMetrics metrics,
        SettingsRegistry registry, string? label = null)
    {
        var values = new Dictionary<string, object>();
        foreach (var def in registry.All)
            values[def.Key] = def.Getter();
        return new Snapshot { Id = id, Elapsed = elapsed, Label = label, Metrics = metrics, SettingsValues = values };
    }
}

public static class SnapshotComparer
{
    public sealed record ComparisonRow(string Label, string ValueA, string ValueB, string Delta);

    public static List<ComparisonRow> Compare(Snapshot a, Snapshot b, SettingsRegistry registry)
    {
        var rows = new List<ComparisonRow>();
        AddMetricRow(rows, "Download Rate", a.Metrics.DownloadRate, b.Metrics.DownloadRate, FormatSpeed);
        AddMetricRow(rows, "Upload Rate", a.Metrics.UploadRate, b.Metrics.UploadRate, FormatSpeed);
        AddMetricRow(rows, "Payload Ratio", a.Metrics.PayloadRatio, b.Metrics.PayloadRatio, v => $"{v:F1}%");
        AddMetricRow(rows, "Pieces/sec", a.Metrics.PiecesPerSecond, b.Metrics.PiecesPerSecond, v => $"{v:F1}");
        AddMetricRow(rows, "Unchoked", a.Metrics.UnchokedCount, b.Metrics.UnchokedCount, v => $"{v:F0}");
        AddMetricRow(rows, "Avg Queue Depth", a.Metrics.AvgQueueDepth, b.Metrics.AvgQueueDepth, v => $"{v:F1}");

        foreach (var def in registry.All)
        {
            if (a.SettingsValues.TryGetValue(def.Key, out var valA) &&
                b.SettingsValues.TryGetValue(def.Key, out var valB) &&
                !Equals(valA, valB))
                rows.Add(new ComparisonRow(def.Label, valA.ToString()!, valB.ToString()!, "changed"));
        }
        return rows;
    }

    private static void AddMetricRow(List<ComparisonRow> rows, string label,
        double a, double b, Func<double, string> format)
    {
        var delta = a == 0 ? "N/A" : $"{(b - a) / a * 100:+0.0;-0.0}%";
        rows.Add(new ComparisonRow(label, format(a), format(b), delta));
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1_048_576) return $"{bytesPerSec / 1_048_576:F1} MB/s";
        if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024:F0} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }
}
