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
/// Result of importing a profile from a .vtprofile.json file.
/// </summary>
public record ImportResult(ProfileSettings? Profile, List<string> Warnings, bool HasNameConflict);

/// <summary>
/// Manages profile persistence: load/save/delete/export/import of ProfileSettings.
/// Built-in presets (Quiet, Balanced, Performance) are always available.
/// Custom profiles and built-in overrides are stored as JSON files.
/// </summary>
public class ProfileManager
{
    private readonly string _profilesDirectory;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly JsonSerializerOptions _checksumJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>
    /// Creates a new ProfileManager. Ensures the profiles directory exists.
    /// </summary>
    /// <param name="dataDirectory">Root data directory (e.g., SessionPersistence data dir).</param>
    public ProfileManager(string dataDirectory)
    {
        _profilesDirectory = Path.Combine(dataDirectory, "settings", "profiles");
        Directory.CreateDirectory(_profilesDirectory);
    }

    /// <summary>
    /// Load all profiles: built-in presets first (with optional customization overrides),
    /// then custom profiles alphabetically.
    /// </summary>
    public async Task<List<ProfileSettings>> LoadAllAsync()
    {
        var result = new List<ProfileSettings>();

        // Built-in presets
        foreach (var preset in ProfilePresets.All)
        {
            var overridePath = Path.Combine(_profilesDirectory, $"_builtin_{preset.Name.ToLowerInvariant()}.json");
            if (File.Exists(overridePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(overridePath).ConfigureAwait(false);
                    var loaded = JsonSerializer.Deserialize<ProfileSettings>(json, _jsonOptions);
                    if (loaded != null)
                    {
                        result.Add(loaded);
                        continue;
                    }
                }
                catch
                {
                    // Fall through to hardcoded preset
                }
            }

            // Use hardcoded preset (deep copy via serialize/deserialize)
            var presetJson = JsonSerializer.Serialize(preset, _jsonOptions);
            var copy = JsonSerializer.Deserialize<ProfileSettings>(presetJson, _jsonOptions)!;
            result.Add(copy);
        }

        // Custom profiles
        var customProfiles = new List<ProfileSettings>();
        if (Directory.Exists(_profilesDirectory))
        {
            foreach (var file in Directory.GetFiles(_profilesDirectory, "*.json").OrderBy(f => f))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith("_builtin_", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                    var profile = JsonSerializer.Deserialize<ProfileSettings>(json, _jsonOptions);
                    if (profile != null)
                        customProfiles.Add(profile);
                }
                catch
                {
                    // Skip corrupt files
                }
            }
        }

        customProfiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        result.AddRange(customProfiles);

        return result;
    }

    /// <summary>
    /// Save a profile to disk. Built-in profiles are saved as override files.
    /// </summary>
    public async Task SaveAsync(ProfileSettings profile)
    {
        string fileName;
        if (ProfilePresets.IsBuiltIn(profile.Name))
        {
            fileName = $"_builtin_{profile.Name.ToLowerInvariant()}.json";
        }
        else
        {
            fileName = SanitizeFileName(profile.Name) + ".json";
        }

        var filePath = Path.Combine(_profilesDirectory, fileName);
        var json = JsonSerializer.Serialize(profile, _jsonOptions);

        // Atomic write
        var tempPath = filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
        File.Move(tempPath, filePath, overwrite: true);
    }

    /// <summary>
    /// Delete a custom profile. Throws if the profile is a built-in preset.
    /// </summary>
    public Task DeleteAsync(string name)
    {
        if (ProfilePresets.IsBuiltIn(name))
            throw new InvalidOperationException($"Cannot delete built-in profile '{name}'.");

        var fileName = SanitizeFileName(name) + ".json";
        var filePath = Path.Combine(_profilesDirectory, fileName);
        if (File.Exists(filePath))
            File.Delete(filePath);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Export a profile to a .vtprofile.json file with checksum.
    /// </summary>
    public async Task ExportAsync(ProfileSettings profile, string filePath)
    {
        var exportData = new ProfileExportData
        {
            ProfileFormatVersion = 1,
            AppVersion = GlobalSettings.CurrentVersion,
            Name = profile.Name,
            Color = profile.Color,
            Scope = profile.Scope,
            Settings = profile.Settings,
            Checksum = ComputeChecksum(profile.Settings)
        };

        var json = JsonSerializer.Serialize(exportData, _jsonOptions);
        await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
    }

    /// <summary>
    /// Import a profile from a .vtprofile.json file with validation.
    /// </summary>
    public async Task<ImportResult> ImportAsync(string filePath)
    {
        var warnings = new List<string>();

        ProfileExportData? exportData;
        try
        {
            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            exportData = JsonSerializer.Deserialize<ProfileExportData>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to parse JSON: {ex.Message}");
            return new ImportResult(null, warnings, false);
        }

        if (exportData == null)
        {
            warnings.Add("File deserialized to null.");
            return new ImportResult(null, warnings, false);
        }

        // Check format version
        if (exportData.ProfileFormatVersion > 1)
        {
            warnings.Add($"Profile format version {exportData.ProfileFormatVersion} is newer than supported (1). Some settings may not be recognized.");
        }

        // Verify checksum
        var expectedChecksum = ComputeChecksum(exportData.Settings);
        if (!string.Equals(exportData.Checksum, expectedChecksum, StringComparison.Ordinal))
        {
            warnings.Add("Checksum mismatch — file may have been manually edited.");
        }

        // Range-validate and clamp settings
        var s = exportData.Settings;
        ClampWithWarning(ref s, nameof(s.MaxGlobalConnections), () => s.MaxGlobalConnections, v => s.MaxGlobalConnections = v, 1, 2000, warnings);
        ClampWithWarning(ref s, nameof(s.MaxConnectionsPerTorrent), () => s.MaxConnectionsPerTorrent, v => s.MaxConnectionsPerTorrent = v, 1, 500, warnings);
        ClampWithWarning(ref s, nameof(s.MaxUploadsPerTorrent), () => s.MaxUploadsPerTorrent, v => s.MaxUploadsPerTorrent = v, 1, 50, warnings);
        ClampWithWarning(ref s, nameof(s.MaxHalfOpenConnections), () => s.MaxHalfOpenConnections, v => s.MaxHalfOpenConnections = v, 1, 200, warnings);
        ClampWithWarning(ref s, nameof(s.ConnectionSpeed), () => s.ConnectionSpeed, v => s.ConnectionSpeed = v, 1, 500, warnings);
        ClampWithWarning(ref s, nameof(s.MaxActiveDownloads), () => s.MaxActiveDownloads, v => s.MaxActiveDownloads = v, 1, 50, warnings);
        ClampWithWarning(ref s, nameof(s.MaxActiveSeeds), () => s.MaxActiveSeeds, v => s.MaxActiveSeeds = v, -1, 100, warnings);
        ClampWithWarning(ref s, nameof(s.MaxActiveTorrents), () => s.MaxActiveTorrents, v => s.MaxActiveTorrents = v, 1, 100, warnings);
        ClampWithWarning(ref s, nameof(s.UnchokeSlots), () => s.UnchokeSlots, v => s.UnchokeSlots = v, 1, 1000, warnings);
        ClampWithWarning(ref s, nameof(s.UnchokeInterval), () => s.UnchokeInterval, v => s.UnchokeInterval = v, 5, 120, warnings);
        ClampWithWarning(ref s, nameof(s.OptimisticUnchokeInterval), () => s.OptimisticUnchokeInterval, v => s.OptimisticUnchokeInterval = v, 10, 300, warnings);
        ClampWithWarning(ref s, nameof(s.PeerTurnover), () => s.PeerTurnover, v => s.PeerTurnover = v, 0, 100, warnings);
        ClampWithWarning(ref s, nameof(s.PeerTurnoverCutoff), () => s.PeerTurnoverCutoff, v => s.PeerTurnoverCutoff = v, 0, 100, warnings);
        ClampWithWarning(ref s, nameof(s.PeerTurnoverInterval), () => s.PeerTurnoverInterval, v => s.PeerTurnoverInterval = v, 30, 3600, warnings);
        ClampWithWarning(ref s, nameof(s.MaxPendingBlocksPerPeer), () => s.MaxPendingBlocksPerPeer, v => s.MaxPendingBlocksPerPeer = v, 1, 2000, warnings);
        ClampLongWithWarning(ref s, nameof(s.CacheSize), () => s.CacheSize, v => s.CacheSize = v, 8L * 1024 * 1024, 1024L * 1024 * 1024, warnings);
        ClampWithWarning(ref s, nameof(s.MaxOutstandingDiskRequests), () => s.MaxOutstandingDiskRequests, v => s.MaxOutstandingDiskRequests = v, 1, 512, warnings);
        ClampWithWarning(ref s, nameof(s.HashThreads), () => s.HashThreads, v => s.HashThreads = v, 1, 8, warnings);
        ClampFloatWithWarning(ref s, nameof(s.SeedRatioLimit), () => s.SeedRatioLimit, v => s.SeedRatioLimit = v, 0f, 50f, warnings);
        ClampWithWarning(ref s, nameof(s.SeedTimeLimit), () => s.SeedTimeLimit, v => s.SeedTimeLimit = v, 0, 10080, warnings);
        ClampWithWarning(ref s, nameof(s.InitialPickerThreshold), () => s.InitialPickerThreshold, v => s.InitialPickerThreshold = v, 0, 100, warnings);
        ClampWithWarning(ref s, nameof(s.WholePiecesThreshold), () => s.WholePiecesThreshold, v => s.WholePiecesThreshold = v, 1, 120, warnings);

        // Check name conflict
        var existing = await LoadAllAsync().ConfigureAwait(false);
        bool hasConflict = existing.Any(p => string.Equals(p.Name, exportData.Name, StringComparison.OrdinalIgnoreCase));

        var profile = new ProfileSettings
        {
            Name = exportData.Name,
            Color = exportData.Color,
            Scope = exportData.Scope,
            Settings = exportData.Settings
        };

        return new ImportResult(profile, warnings, hasConflict);
    }

    /// <summary>
    /// Compute a deterministic SHA-256 checksum of settings values.
    /// </summary>
    private string ComputeChecksum(ProfileSettingsValues settings)
    {
        // Serialize with sorted keys for determinism
        var sortedOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        var json = JsonSerializer.Serialize(settings, sortedOptions);

        // Sort keys for determinism: parse to dictionary, sort, re-serialize
        using var doc = JsonDocument.Parse(json);
        var sorted = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            sorted[prop.Name] = prop.Value.Clone();
        }

        var sortedJson = JsonSerializer.Serialize(sorted, new JsonSerializerOptions { WriteIndented = false });

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sortedJson));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Sanitize a profile name for use as a filename.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }

        var result = sb.ToString().Trim();
        if (result.Length > 50)
            result = result.Substring(0, 50);

        return result;
    }

    private static void ClampWithWarning(ref ProfileSettingsValues s, string name, Func<int> getter, Action<int> setter, int min, int max, List<string> warnings)
    {
        var value = getter();
        if (value < min || value > max)
        {
            setter(Math.Clamp(value, min, max));
            warnings.Add($"{name} value {value} out of range [{min},{max}], clamped to {getter()}.");
        }
    }

    private static void ClampLongWithWarning(ref ProfileSettingsValues s, string name, Func<long> getter, Action<long> setter, long min, long max, List<string> warnings)
    {
        var value = getter();
        if (value < min || value > max)
        {
            setter(Math.Clamp(value, min, max));
            warnings.Add($"{name} value {value} out of range [{min},{max}], clamped to {getter()}.");
        }
    }

    private static void ClampFloatWithWarning(ref ProfileSettingsValues s, string name, Func<float> getter, Action<float> setter, float min, float max, List<string> warnings)
    {
        var value = getter();
        if (value < min || value > max)
        {
            setter(Math.Clamp(value, min, max));
            warnings.Add($"{name} value {value} out of range [{min},{max}], clamped to {getter()}.");
        }
    }
}
