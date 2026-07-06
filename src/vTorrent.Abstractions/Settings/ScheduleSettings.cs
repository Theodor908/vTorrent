namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Determines how a schedule cell affects torrent behavior.
/// </summary>
public enum ScheduleCellMode
{
    /// <summary>Activate a named performance profile.</summary>
    Profile,

    /// <summary>Pause downloads, keep uploads running.</summary>
    SeedOnly,

    /// <summary>Stop all unforced torrents.</summary>
    Paused
}

/// <summary>
/// A single cell in the weekly schedule grid (one hour of one day).
/// </summary>
public class ScheduleCell
{
    /// <summary>What action to take during this hour.</summary>
    public ScheduleCellMode Mode { get; set; } = ScheduleCellMode.Profile;

    /// <summary>Profile name to activate when Mode is Profile. Null for SeedOnly/Paused.</summary>
    public string? ProfileName { get; set; } = "Balanced";
}

/// <summary>
/// Weekly schedule settings for automatic profile switching.
/// Grid is 7 days (Sun-Sat) × 24 hours.
/// </summary>
public class ScheduleSettings
{
    /// <summary>Whether the scheduler is active.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 7×24 jagged array: Grid[day][hour] where day 0 = Sunday, hour 0 = midnight.
    /// </summary>
    public ScheduleCell[][] Grid { get; set; } = CreateDefaultGrid();

    /// <summary>
    /// Creates a default 7×24 grid with all cells set to Profile("Balanced").
    /// </summary>
    public static ScheduleCell[][] CreateDefaultGrid()
    {
        var grid = new ScheduleCell[7][];
        for (int day = 0; day < 7; day++)
        {
            grid[day] = new ScheduleCell[24];
            for (int hour = 0; hour < 24; hour++)
            {
                grid[day][hour] = new ScheduleCell();
            }
        }
        return grid;
    }
}
