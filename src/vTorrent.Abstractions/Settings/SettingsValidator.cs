using Microsoft.Extensions.Logging;

namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Clamp-and-log validation for settings. Never throws — logs warning and clamps to valid range.
/// </summary>
public static class SettingsValidator
{
    public static int Clamp(int value, int min, int max, string name, ILogger logger)
    {
        if (value >= min && value <= max) return value;
        logger.LogWarning("Setting {Name} value {Value} out of range [{Min},{Max}], clamped",
            name, value, min, max);
        return Math.Clamp(value, min, max);
    }

    public static double Clamp(double value, double min, double max, string name, ILogger logger)
    {
        if (value >= min && value <= max) return value;
        logger.LogWarning("Setting {Name} value {Value} out of range [{Min},{Max}], clamped",
            name, value, min, max);
        return Math.Clamp(value, min, max);
    }

    public static long Clamp(long value, long min, long max, string name, ILogger logger)
    {
        if (value >= min && value <= max) return value;
        logger.LogWarning("Setting {Name} value {Value} out of range [{Min},{Max}], clamped",
            name, value, min, max);
        return Math.Clamp(value, min, max);
    }
}
