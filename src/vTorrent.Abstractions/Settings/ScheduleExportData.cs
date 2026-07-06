using System.Collections.Generic;

namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Serialization model for .vtschedule.json files.
/// Contains the 7x24 schedule grid and all referenced custom profiles.
/// </summary>
public class ScheduleExportData
{
    public int ScheduleFormatVersion { get; set; } = 1;
    public int AppVersion { get; set; } = GlobalSettings.CurrentVersion;
    public string Checksum { get; set; } = "";
    public ScheduleCell[][] Grid { get; set; } = System.Array.Empty<ScheduleCell[]>();
    public List<ProfileSettings> Profiles { get; set; } = new();
}

/// <summary>
/// Result of importing a schedule package.
/// </summary>
public class ScheduleImportResult
{
    public bool Success { get; set; }
    public List<string> ImportedProfiles { get; set; } = new();
    public Dictionary<string, string> RenamedProfiles { get; set; } = new();
    public List<string> SkippedProfiles { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
