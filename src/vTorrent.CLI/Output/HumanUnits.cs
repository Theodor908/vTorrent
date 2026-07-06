// src/vTorrent.CLI/Output/HumanUnits.cs
using System;
using System.Globalization;

namespace vTorrent.Cli.Output;

public static class HumanUnits
{
    private static readonly string[] ByteSuffixes = { "B", "KB", "MB", "GB", "TB" };

    public static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";

        double value = bytes;
        int order = 0;
        while (value >= 1024 && order < ByteSuffixes.Length - 1)
        {
            value /= 1024;
            order++;
        }

        return order == 0
            ? string.Format(CultureInfo.InvariantCulture, "{0:F0} {1}", value, ByteSuffixes[order])
            : string.Format(CultureInfo.InvariantCulture, "{0:F2} {1}", value, ByteSuffixes[order]);
    }

    public static string FormatSpeed(int bytesPerSec)
    {
        if (bytesPerSec == 0) return "-";
        return FormatBytes(bytesPerSec) + "/s";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{(int)duration.TotalSeconds}s";
    }

    public static string FormatRatio(double ratio)
        => ratio.ToString("F2", CultureInfo.InvariantCulture);

    public static string FormatProgress(double progress)
        => $"{(int)(progress * 100)}%";

    public static string FormatDateTime(DateTime dt)
        => dt.ToString("yyyy-MM-dd HH:mm");
}
