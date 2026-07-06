using System;
using System.IO;
using System.Text.Json;

namespace vTorrent.Bench.Config;

public static class ScenarioLoader
{
    public static ScenarioConfig Load(
        string? scenarioPath,
        string? presetName,
        int? peers,
        int? pieceCount,
        int? pieceSize,
        string? torrentPath,
        string? dataPath)
    {
        ScenarioConfig config;

        if (scenarioPath != null)
        {
            var json = File.ReadAllText(scenarioPath);
            config = JsonSerializer.Deserialize<ScenarioConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException($"Failed to parse scenario: {scenarioPath}");
        }
        else if (presetName != null && Enum.TryParse<ScenarioPreset>(presetName, ignoreCase: true, out var preset))
        {
            config = Presets.Get(preset);
        }
        else
        {
            config = Presets.Get(ScenarioPreset.HomeDSL);
        }

        if (peers.HasValue) config.PeerCount = peers.Value;
        if (pieceCount.HasValue) config.PieceCount = pieceCount.Value;
        if (pieceSize.HasValue) config.PieceSize = pieceSize.Value;
        if (torrentPath != null) config.TorrentFilePath = torrentPath;
        if (dataPath != null) config.DataPath = dataPath;

        return config;
    }
}
