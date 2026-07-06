using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using vTorrent.Desktop.ViewModels;

namespace vTorrent.Desktop.Views;

/// <summary>
/// Converts a hex color string (e.g., "#2196F3") to an IBrush.
/// Used in code-behind where XAML's automatic string→Brush conversion isn't available.
/// </summary>
public class StringToBrushConverter : IValueConverter
{
    public static readonly StringToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string colorStr && !string.IsNullOrEmpty(colorStr))
            return SolidColorBrush.Parse(colorStr);
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts TorrentDisplayState to Phosphor icon character
/// </summary>
public class StateToIconConverter : IValueConverter
{
    public static readonly StateToIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TorrentDisplayState state)
        {
            return state switch
            {
                TorrentDisplayState.Downloading => "\uE03E", // Arrow down
                TorrentDisplayState.ForcedDownloading => "\uE03E", // Arrow down (forced)
                TorrentDisplayState.Seeding => "\uE08E",     // Arrow up
                TorrentDisplayState.ForcedSeeding => "\uE08E", // Arrow up (forced)
                TorrentDisplayState.Paused => "\uE3A0",      // Pause
                TorrentDisplayState.Verifying => "\uE2BA",   // Hourglass
                TorrentDisplayState.Checking => "\uE2BA",    // Hourglass (force recheck)
                TorrentDisplayState.Queued => "\uE19A",      // Clock
                TorrentDisplayState.Error => "\uE4E4",       // Warning
                TorrentDisplayState.Stalled => "\uE3DE",     // Stalled
                TorrentDisplayState.Allocating => "\uE24A",  // Folder
                TorrentDisplayState.Moving => "\uE256",      // Folder open
                TorrentDisplayState.MetadataDownloading => "\uE03E", // Arrow down (fetching metadata from peers)
                TorrentDisplayState.Stopping => "\uE3A0",         // Pause (same as Paused)
                TorrentDisplayState.CheckingResumeData => "\uE2BA", // Hourglass (same as Verifying)
                TorrentDisplayState.StalledSeeding => "\uE3DE",    // Stalled (same as Stalled)
                TorrentDisplayState.MissingFiles => "\uE4E4",      // Warning (same as Error)
                TorrentDisplayState.Connecting => "\uE03E",        // Arrow down (same as Downloading)
                TorrentDisplayState.Stopped => "\uE3A0",           // Pause (same as Paused)
                _ => "\uE230"                                 // File
            };
        }
        return "\uE230";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Returns true if state is Downloading
/// </summary>
public class StateToDownloadingConverter : IValueConverter
{
    public static readonly StateToDownloadingConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is TorrentDisplayState state && state is TorrentDisplayState.Downloading or TorrentDisplayState.ForcedDownloading;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Returns true if state is Seeding
/// </summary>
public class StateToSeedingConverter : IValueConverter
{
    public static readonly StateToSeedingConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is TorrentDisplayState state && state is TorrentDisplayState.Seeding or TorrentDisplayState.ForcedSeeding;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Returns true if state is Paused
/// </summary>
public class StateToPausedConverter : IValueConverter
{
    public static readonly StateToPausedConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is TorrentDisplayState state && state == TorrentDisplayState.Paused;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts TorrentDisplayState to progress bar brush color.
/// Always returns a valid brush to prevent disappearing progress bars.
/// Theme-aware: returns dark or light brushes based on the active theme.
/// </summary>
public class StateToProgressColorConverter : IValueConverter
{
    public static readonly StateToProgressColorConverter Instance = new();

    // Dark theme brushes
    private static readonly IBrush DarkDownloadingBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.Parse("#00d9ff"), 0),
            new GradientStop(Color.Parse("#38bdf8"), 1)
        }
    };

    private static readonly IBrush DarkSeedingBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.Parse("#10B981"), 0),
            new GradientStop(Color.Parse("#34D399"), 1)
        }
    };

    private static readonly IBrush DarkPausedBrush = new SolidColorBrush(Color.Parse("#4B5563"));
    private static readonly IBrush DarkQueuedBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush DarkStoppedBrush = new SolidColorBrush(Color.Parse("#374151"));

    private static readonly IBrush DarkStalledBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.Parse("#F59E0B"), 0),
            new GradientStop(Color.Parse("#D97706"), 1)
        }
    };

    private static readonly IBrush DarkProcessingBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.Parse("#8B5CF6"), 0),
            new GradientStop(Color.Parse("#A78BFA"), 1)
        }
    };

    private static readonly IBrush DarkErrorBrush = new SolidColorBrush(Color.Parse("#EF4444"));

    // Light theme brushes
    private static readonly IBrush LightDownloadingBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.Parse("#4A90A4"), 0),
            new GradientStop(Color.Parse("#5BA3B7"), 1)
        }
    };

    private static readonly IBrush LightSeedingBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.Parse("#48BB78"), 0),
            new GradientStop(Color.Parse("#68D391"), 1)
        }
    };

    private static readonly IBrush LightPausedBrush = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush LightQueuedBrush = new SolidColorBrush(Color.Parse("#D1D5DB"));
    private static readonly IBrush LightStoppedBrush = new SolidColorBrush(Color.Parse("#6B7280"));

    private static readonly IBrush LightStalledBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.Parse("#D97706"), 0),
            new GradientStop(Color.Parse("#B45309"), 1)
        }
    };

    private static readonly IBrush LightProcessingBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.Parse("#7C3AED"), 0),
            new GradientStop(Color.Parse("#8B5CF6"), 1)
        }
    };

    private static readonly IBrush LightErrorBrush = new SolidColorBrush(Color.Parse("#F56565"));

    private static bool IsDarkTheme()
    {
        return Application.Current?.ActualThemeVariant != ThemeVariant.Light;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TorrentDisplayState state)
        {
            bool dark = IsDarkTheme();
            return state switch
            {
                TorrentDisplayState.Downloading => dark ? DarkDownloadingBrush : LightDownloadingBrush,
                TorrentDisplayState.ForcedDownloading => dark ? DarkDownloadingBrush : LightDownloadingBrush,
                TorrentDisplayState.Connecting => dark ? DarkDownloadingBrush : LightDownloadingBrush,
                TorrentDisplayState.Seeding => dark ? DarkSeedingBrush : LightSeedingBrush,
                TorrentDisplayState.ForcedSeeding => dark ? DarkSeedingBrush : LightSeedingBrush,
                TorrentDisplayState.Paused => dark ? DarkPausedBrush : LightPausedBrush,
                TorrentDisplayState.Stopping => dark ? DarkPausedBrush : LightPausedBrush,
                TorrentDisplayState.Queued => dark ? DarkQueuedBrush : LightQueuedBrush,
                TorrentDisplayState.Stopped => dark ? DarkStoppedBrush : LightStoppedBrush,
                TorrentDisplayState.Stalled => dark ? DarkStalledBrush : LightStalledBrush,
                TorrentDisplayState.StalledSeeding => dark ? DarkStalledBrush : LightStalledBrush,
                TorrentDisplayState.Verifying => dark ? DarkProcessingBrush : LightProcessingBrush,
                TorrentDisplayState.Checking => dark ? DarkProcessingBrush : LightProcessingBrush,
                TorrentDisplayState.CheckingResumeData => dark ? DarkProcessingBrush : LightProcessingBrush,
                TorrentDisplayState.Allocating => dark ? DarkProcessingBrush : LightProcessingBrush,
                TorrentDisplayState.Moving => dark ? DarkProcessingBrush : LightProcessingBrush,
                TorrentDisplayState.MetadataDownloading => dark ? DarkProcessingBrush : LightProcessingBrush,
                TorrentDisplayState.Error => dark ? DarkErrorBrush : LightErrorBrush,
                TorrentDisplayState.MissingFiles => dark ? DarkErrorBrush : LightErrorBrush,
                _ => dark ? DarkDownloadingBrush : LightDownloadingBrush
            };
        }
        return IsDarkTheme() ? DarkDownloadingBrush : LightDownloadingBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
