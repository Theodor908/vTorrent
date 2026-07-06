// src/vTorrent.CLI/Client/ProfileModels.cs
using System.Text.Json.Serialization;

namespace vTorrent.Cli.Client;

public record ProfileMeta(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("scope")] string Scope);

public record ActiveProfileState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("scheduleEnabled")] bool ScheduleEnabled);

public record ScheduleGridCell(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("profileName")] string? ProfileName,
    [property: JsonPropertyName("color")] string Color);

public record ScheduleData(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("grid")] ScheduleGridCell[][] Grid);

public record ProfileImportResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("warnings")] string[] Warnings,
    [property: JsonPropertyName("hadNameConflict")] bool HadNameConflict);
