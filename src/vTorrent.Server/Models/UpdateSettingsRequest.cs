using System.Text.Json.Nodes;

namespace vTorrent.Server.Models;

/// <summary>
/// Partial settings update — JSON object with settings groups as keys.
/// Fields set to "***" are ignored (redacted fields not being changed).
/// </summary>
public record UpdateSettingsRequest
{
    public JsonObject Settings { get; init; } = new();
}
