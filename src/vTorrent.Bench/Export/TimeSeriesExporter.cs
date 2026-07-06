using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace vTorrent.Bench.Export;

public sealed class TimeSeriesExporter
{
    private readonly List<Sample> _samples = new();

    public void Record(long elapsedMs, double downloadRate, double uploadRate,
        double payloadRatio, int piecesCompleted, int activeConnections,
        int unchoked, double avgQueueDepth, string? change = null)
    {
        _samples.Add(new Sample(elapsedMs, downloadRate, uploadRate, payloadRatio,
            piecesCompleted, activeConnections, unchoked, avgQueueDepth, change));
    }

    public void Export(string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("elapsed_ms,download_rate,upload_rate,payload_ratio,pieces_completed,active_connections,unchoked,avg_queue_depth,change");
        foreach (var s in _samples)
        {
            sb.Append(s.ElapsedMs).Append(',');
            sb.Append(s.DownloadRate.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(s.UploadRate.ToString("F0", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(s.PayloadRatio.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(s.PiecesCompleted).Append(',');
            sb.Append(s.ActiveConnections).Append(',');
            sb.Append(s.Unchoked).Append(',');
            sb.Append(s.AvgQueueDepth.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
            if (s.Change != null) sb.Append('"').Append(s.Change.Replace("\"", "\"\"")).Append('"');
            sb.AppendLine();
        }
        File.WriteAllText(filePath, sb.ToString());
    }

    private sealed record Sample(long ElapsedMs, double DownloadRate, double UploadRate,
        double PayloadRatio, int PiecesCompleted, int ActiveConnections,
        int Unchoked, double AvgQueueDepth, string? Change);
}
