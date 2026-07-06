using System.Text.Json;
using FluentAssertions;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Settings;
using Xunit;

namespace vTorrent.Core.Tests.Settings;

public class ProfileManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProfileManager _manager;

    public ProfileManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "vtorrent_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _manager = new ProfileManager(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public async Task LoadAllAsync_ReturnsBuiltInPresetsWhenEmpty()
    {
        var profiles = await _manager.LoadAllAsync();

        profiles.Should().HaveCount(3);
        profiles[0].Name.Should().Be("Quiet");
        profiles[1].Name.Should().Be("Balanced");
        profiles[2].Name.Should().Be("Performance");
    }

    [Fact]
    public async Task SaveCustomAsync_CreatesFileAndReloads()
    {
        var custom = new ProfileSettings
        {
            Name = "MySeedbox",
            Color = "#FF5722",
            Scope = "performance",
            Settings = new ProfileSettingsValues { MaxGlobalConnections = 1500 }
        };

        await _manager.SaveAsync(custom);
        var profiles = await _manager.LoadAllAsync();

        profiles.Should().HaveCount(4);
        profiles.Last().Name.Should().Be("MySeedbox");
        profiles.Last().Settings.MaxGlobalConnections.Should().Be(1500);
    }

    [Fact]
    public async Task DeleteAsync_RemovesCustomProfile()
    {
        var custom = new ProfileSettings
        {
            Name = "ToDelete",
            Color = "#FF0000",
            Settings = new ProfileSettingsValues()
        };

        await _manager.SaveAsync(custom);
        var before = await _manager.LoadAllAsync();
        before.Should().HaveCount(4);

        await _manager.DeleteAsync("ToDelete");
        var after = await _manager.LoadAllAsync();
        after.Should().HaveCount(3);
        after.Should().NotContain(p => p.Name == "ToDelete");
    }

    [Fact]
    public async Task DeleteAsync_RefusesBuiltIn()
    {
        var act = () => _manager.DeleteAsync("Balanced");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExportAsync_ProducesValidJson()
    {
        var profile = new ProfileSettings
        {
            Name = "ExportMe",
            Color = "#009688",
            Scope = "performance",
            Settings = new ProfileSettingsValues { MaxGlobalConnections = 800 }
        };

        var exportPath = Path.Combine(_tempDir, "export_test.vtprofile.json");
        await _manager.ExportAsync(profile, exportPath);

        File.Exists(exportPath).Should().BeTrue();
        var json = await File.ReadAllTextAsync(exportPath);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("profileFormatVersion").GetInt32().Should().Be(1);
        root.GetProperty("name").GetString().Should().Be("ExportMe");
        root.GetProperty("checksum").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("settings").GetProperty("maxGlobalConnections").GetInt32().Should().Be(800);
    }

    [Fact]
    public async Task ImportAsync_LoadsValidFile()
    {
        var profile = new ProfileSettings
        {
            Name = "RoundTrip",
            Color = "#673AB7",
            Scope = "performance",
            Settings = new ProfileSettingsValues
            {
                MaxGlobalConnections = 1000,
                HashThreads = 4,
                CacheSize = 128 * 1024 * 1024
            }
        };

        var exportPath = Path.Combine(_tempDir, "roundtrip.vtprofile.json");
        await _manager.ExportAsync(profile, exportPath);

        var result = await _manager.ImportAsync(exportPath);

        result.Profile.Should().NotBeNull();
        result.Profile!.Name.Should().Be("RoundTrip");
        result.Profile.Settings.MaxGlobalConnections.Should().Be(1000);
        result.Profile.Settings.HashThreads.Should().Be(4);
        result.Profile.Settings.CacheSize.Should().Be(128 * 1024 * 1024);
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_ClampsOutOfRangeValues()
    {
        var profile = new ProfileSettings
        {
            Name = "Clamped",
            Color = "#FF0000",
            Settings = new ProfileSettingsValues { MaxGlobalConnections = 99999 }
        };

        var exportPath = Path.Combine(_tempDir, "clamped.vtprofile.json");
        await _manager.ExportAsync(profile, exportPath);

        // Manually edit the exported JSON to have out-of-range value
        var json = await File.ReadAllTextAsync(exportPath);
        json = json.Replace("\"maxGlobalConnections\": 99999", "\"maxGlobalConnections\": 99999");
        // The value 99999 is already out of range, but the checksum will match from export.
        // Let's re-export with the bad value directly in settings JSON.
        var doc = JsonDocument.Parse(json);
        // Actually, let's just manually construct the JSON with a bad value and bad checksum
        var badJson = json.Replace("\"maxGlobalConnections\": 2000", "\"maxGlobalConnections\": 99999");
        if (!badJson.Contains("99999"))
        {
            // If the original already had 99999, we just need to make sure it stays
            // The export would have the original value. Let's just write directly.
            badJson = json;
        }
        await File.WriteAllTextAsync(exportPath, badJson);

        var result = await _manager.ImportAsync(exportPath);

        result.Profile.Should().NotBeNull();
        result.Profile!.Settings.MaxGlobalConnections.Should().BeLessOrEqualTo(2000);
        result.Warnings.Should().Contain(w => w.Contains("MaxGlobalConnections", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportAsync_WarnsOnChecksumMismatch()
    {
        var profile = new ProfileSettings
        {
            Name = "ChecksumTest",
            Color = "#FF0000",
            Settings = new ProfileSettingsValues()
        };

        var exportPath = Path.Combine(_tempDir, "checksum_test.vtprofile.json");
        await _manager.ExportAsync(profile, exportPath);

        // Tamper with the checksum
        var json = await File.ReadAllTextAsync(exportPath);
        json = json.Replace("\"checksum\": \"", "\"checksum\": \"0000");
        await File.WriteAllTextAsync(exportPath, json);

        var result = await _manager.ImportAsync(exportPath);

        result.Profile.Should().NotBeNull();
        result.Warnings.Should().Contain(w => w.Contains("checksum", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportAsync_DetectsNameConflict()
    {
        var existing = new ProfileSettings
        {
            Name = "Duplicate",
            Color = "#FF0000",
            Settings = new ProfileSettingsValues()
        };
        await _manager.SaveAsync(existing);

        var exportPath = Path.Combine(_tempDir, "duplicate.vtprofile.json");
        await _manager.ExportAsync(existing, exportPath);

        var result = await _manager.ImportAsync(exportPath);

        result.HasNameConflict.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_BuiltInCustomization_OverridesPreset()
    {
        var quietCustom = new ProfileSettings
        {
            Name = "Quiet",
            Color = "#78909C",
            Scope = "performance",
            Settings = new ProfileSettingsValues
            {
                MaxGlobalConnections = 75,  // different from preset's 100
                MaxConnectionsPerTorrent = 25
            }
        };

        await _manager.SaveAsync(quietCustom);
        var profiles = await _manager.LoadAllAsync();

        var quiet = profiles.First(p => p.Name == "Quiet");
        quiet.Settings.MaxGlobalConnections.Should().Be(75);
        quiet.Settings.MaxConnectionsPerTorrent.Should().Be(25);
    }

    [Fact]
    public async Task LoadAllAsync_BuiltInWithoutCustomization_UsesPresetDefaults()
    {
        // No customization files exist — should use hardcoded preset defaults
        var profiles = await _manager.LoadAllAsync();

        var quiet = profiles.First(p => p.Name == "Quiet");
        quiet.Settings.MaxGlobalConnections.Should().Be(100);  // Quiet preset default
        quiet.Settings.GlobalDownloadLimit.Should().Be(1 * 1024 * 1024);  // 1 MB/s

        var balanced = profiles.First(p => p.Name == "Balanced");
        balanced.Settings.MaxGlobalConnections.Should().Be(500);  // Balanced default

        var performance = profiles.First(p => p.Name == "Performance");
        performance.Settings.MaxGlobalConnections.Should().Be(2000);  // Performance default
    }
}
