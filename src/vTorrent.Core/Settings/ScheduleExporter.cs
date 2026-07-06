using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Settings;

/// <summary>
/// Export and import schedule packages (.vtschedule.json) containing
/// the 7x24 grid and all referenced custom profiles.
/// </summary>
public class ScheduleExporter
{
    private readonly ProfileManager _profileManager;
    private readonly SettingsManager _settingsManager;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ScheduleExporter(ProfileManager profileManager, SettingsManager settingsManager)
    {
        _profileManager = profileManager;
        _settingsManager = settingsManager;
    }

    /// <summary>Export the current schedule to a file.</summary>
    public async Task ExportAsync(string filePath)
    {
        using var stream = File.Create(filePath);
        await ExportToStreamAsync(stream).ConfigureAwait(false);
    }

    /// <summary>Export the current schedule to a stream (for WebUI download).</summary>
    public async Task ExportToStreamAsync(Stream stream)
    {
        var settings = _settingsManager.Current;
        var allProfiles = await _profileManager.LoadAllAsync().ConfigureAwait(false);

        // Collect unique custom profile names referenced by the grid
        var referencedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var day in settings.Schedule.Grid)
        {
            foreach (var cell in day)
            {
                if (cell.Mode == ScheduleCellMode.Profile && !string.IsNullOrEmpty(cell.ProfileName))
                {
                    if (!ProfilePresets.IsBuiltIn(cell.ProfileName))
                        referencedNames.Add(cell.ProfileName);
                }
            }
        }

        // Collect the actual profile objects
        var profiles = allProfiles
            .Where(p => referencedNames.Contains(p.Name))
            .ToList();

        var exportData = new ScheduleExportData
        {
            ScheduleFormatVersion = 1,
            AppVersion = GlobalSettings.CurrentVersion,
            Grid = settings.Schedule.Grid,
            Profiles = profiles,
        };

        exportData.Checksum = ComputeChecksum(exportData);
        await JsonSerializer.SerializeAsync(stream, exportData, JsonOptions).ConfigureAwait(false);
    }

    /// <summary>Import a schedule from a file.</summary>
    public async Task<ScheduleImportResult> ImportAsync(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return await ImportFromStreamAsync(stream).ConfigureAwait(false);
    }

    /// <summary>Import a schedule from a stream (for WebUI upload).</summary>
    public async Task<ScheduleImportResult> ImportFromStreamAsync(Stream stream)
    {
        var result = new ScheduleImportResult();

        // Parse
        ScheduleExportData? exportData;
        try
        {
            exportData = await JsonSerializer.DeserializeAsync<ScheduleExportData>(stream, JsonOptions)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Failed to parse JSON: {ex.Message}");
            return result;
        }

        if (exportData == null)
        {
            result.Warnings.Add("File deserialized to null.");
            return result;
        }

        // Version check
        if (exportData.ScheduleFormatVersion > 1)
        {
            result.Warnings.Add($"Schedule format version {exportData.ScheduleFormatVersion} is newer than supported (1). Some data may not be recognized.");
        }

        // Checksum verification
        var expectedChecksum = ComputeChecksum(exportData);
        if (!string.Equals(exportData.Checksum, expectedChecksum, StringComparison.Ordinal))
        {
            result.Warnings.Add("Checksum mismatch — file may have been manually edited.");
        }

        // Validate grid dimensions
        if (exportData.Grid == null || exportData.Grid.Length != 7)
        {
            result.Warnings.Add("Invalid grid: expected 7 days.");
            return result;
        }
        foreach (var day in exportData.Grid)
        {
            if (day == null || day.Length != 24)
            {
                result.Warnings.Add("Invalid grid: each day must have 24 hours.");
                return result;
            }
        }

        // Import profiles with conflict resolution
        var existingProfiles = await _profileManager.LoadAllAsync().ConfigureAwait(false);
        var nameRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in exportData.Profiles)
        {
            // Validate profile name for filesystem safety
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                result.Warnings.Add("Skipped profile with empty name.");
                continue;
            }

            if (profile.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                profile.Name.Contains("..") ||
                profile.Name.Contains('/') ||
                profile.Name.Contains('\\'))
            {
                result.Warnings.Add($"Skipped profile '{profile.Name}': name contains invalid characters.");
                continue;
            }

            var existing = existingProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                // No conflict — import as-is
                await _profileManager.SaveAsync(profile).ConfigureAwait(false);
                result.ImportedProfiles.Add(profile.Name);
            }
            else if (existing.Settings.ValueEquals(profile.Settings))
            {
                // Identical settings — skip
                result.SkippedProfiles.Add(profile.Name);
            }
            else
            {
                // Conflict — rename and save
                var newName = $"{profile.Name} (imported)";
                // Ensure unique name
                int suffix = 2;
                while (existingProfiles.Any(p =>
                    string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
                {
                    newName = $"{profile.Name} (imported {suffix++})";
                }

                var originalName = profile.Name;
                nameRemap[originalName] = newName;
                profile.Name = newName;
                await _profileManager.SaveAsync(profile).ConfigureAwait(false);
                result.ImportedProfiles.Add(newName);
                result.RenamedProfiles[originalName] = newName;
            }
        }

        // Remap grid cell profile names for any renamed profiles
        if (nameRemap.Count > 0)
        {
            foreach (var day in exportData.Grid)
            {
                foreach (var cell in day)
                {
                    if (cell.Mode == ScheduleCellMode.Profile &&
                        !string.IsNullOrEmpty(cell.ProfileName) &&
                        nameRemap.TryGetValue(cell.ProfileName, out var newName))
                    {
                        cell.ProfileName = newName;
                    }
                }
            }
        }

        // Build the set of valid profile names: built-ins + existing + just-imported
        var allKnownProfiles = await _profileManager.LoadAllAsync().ConfigureAwait(false);
        var validProfileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in allKnownProfiles)
            validProfileNames.Add(p.Name);

        // Validate grid cell profileName references
        foreach (var day in exportData.Grid)
        {
            foreach (var cell in day)
            {
                if (cell.Mode == ScheduleCellMode.Profile && !string.IsNullOrEmpty(cell.ProfileName))
                {
                    if (!ProfilePresets.IsBuiltIn(cell.ProfileName) &&
                        !validProfileNames.Contains(cell.ProfileName))
                    {
                        result.Warnings.Add($"Grid cell referenced unknown profile '{cell.ProfileName}'; defaulted to 'Balanced'.");
                        cell.ProfileName = "Balanced";
                    }
                }
            }
        }

        // Apply grid to settings (leave Schedule.Enabled unchanged)
        var currentEnabled = _settingsManager.Current.Schedule.Enabled;
        await _settingsManager.UpdateAndSaveAsync(gs =>
        {
            gs.Schedule.Grid = exportData.Grid;
            gs.Schedule.Enabled = currentEnabled; // Preserve current state
        }).ConfigureAwait(false);

        result.Success = true;
        return result;
    }

    /// <summary>Compute SHA-256 checksum over grid + profiles for integrity.</summary>
    private string ComputeChecksum(ScheduleExportData data)
    {
        var checksumOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        // Checksum covers grid + profiles (not version/checksum fields)
        var payload = new
        {
            grid = data.Grid,
            profiles = data.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList()
        };

        var json = JsonSerializer.Serialize(payload, checksumOptions);

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
