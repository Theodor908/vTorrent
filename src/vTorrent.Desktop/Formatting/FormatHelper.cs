namespace vTorrent.Desktop.Formatting;

/// <summary>
/// Display formatting utilities for the Desktop UI.
/// Core exposes raw values in DTOs — this class converts them to human-readable strings.
/// </summary>
public static class FormatHelper
{
    public static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int suffixIndex = 0;
        double value = bytes;

        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }

        return suffixIndex == 0
            ? $"{value:F0} {suffixes[suffixIndex]}"
            : $"{value:F2} {suffixes[suffixIndex]}";
    }

    public static string FormatBytesPrecise(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int suffixIndex = 0;
        double value = bytes;

        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }

        return suffixIndex == 0
            ? $"{value:F0} {suffixes[suffixIndex]}"
            : $"{value:F2} {suffixes[suffixIndex]}";
    }

    public static string FormatSpeed(int bytesPerSecond)
    {
        if (bytesPerSecond == 0) return "-";
        return $"{FormatBytes(bytesPerSecond)}/s";
    }

    public static string FormatSpeed(long bytesPerSecond)
    {
        return $"{FormatBytes(bytesPerSecond)}/s";
    }

    public static string FormatTimeSpan(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours} hr {time.Minutes} min";
        if (time.TotalMinutes >= 1)
            return $"{(int)time.TotalMinutes} min {time.Seconds} sec";
        return $"{(int)time.TotalSeconds} sec";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{(int)duration.TotalSeconds}s";
    }
}
