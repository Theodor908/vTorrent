using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using vTorrent.Bench.Settings;

namespace vTorrent.Bench.Export;

public static class ProfileExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Export(SettingsRegistry registry, string name, string filePath)
    {
        var settings = new Dictionary<string, object>();
        foreach (var def in registry.All)
            settings[def.Key] = def.Getter();

        var settingsJson = JsonSerializer.Serialize(
            new SortedDictionary<string, object>(settings), JsonOpts);
        var checksumBytes = SHA256.HashData(Encoding.UTF8.GetBytes(settingsJson));
        var checksum = Convert.ToHexString(checksumBytes).ToLowerInvariant();

        var export = new
        {
            profileFormatVersion = 1,
            appVersion = 1,
            name,
            color = "#7ee787",
            scope = "performance",
            checksum,
            settings,
        };

        File.WriteAllText(filePath, JsonSerializer.Serialize(export, JsonOpts));
    }
}
