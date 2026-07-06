// src/vTorrent.CLI/Client/TokenStore.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace vTorrent.Cli.Client;

public record StoredToken
{
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public long ExpiresAt { get; init; }
    public string? ApiKey { get; init; }

    [JsonIgnore]
    public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeSeconds() > ExpiresAt;

    [JsonIgnore]
    public bool IsExpiringSoon => !IsExpired &&
        DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() > ExpiresAt;

    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);
}

public class TokenStore
{
    private readonly string _filePath;
    private Dictionary<string, StoredToken> _tokens;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TokenStore(string configDir)
    {
        _filePath = Path.Combine(configDir, "tokens.json");
        _tokens = Load();
    }

    public void Save(string profileName, string accessToken, string refreshToken, long expiresAt)
    {
        var existing = Load(profileName);
        _tokens[profileName] = new StoredToken
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt,
            ApiKey = existing?.ApiKey
        };
        Persist();
    }

    public void SaveApiKey(string profileName, string apiKey)
    {
        var existing = Load(profileName);
        _tokens[profileName] = new StoredToken
        {
            AccessToken = existing?.AccessToken ?? "",
            RefreshToken = existing?.RefreshToken ?? "",
            ExpiresAt = long.MaxValue,
            ApiKey = apiKey
        };
        Persist();
    }

    public void ClearApiKey(string profileName)
    {
        var existing = Load(profileName);
        if (existing == null) return;
        _tokens[profileName] = existing with { ApiKey = null };
        Persist();
    }

    public StoredToken? Load(string profileName)
        => _tokens.TryGetValue(profileName, out var t) ? t : null;

    /// <summary>
    /// Re-read tokens from disk. Call when another component (e.g., a command)
    /// may have written to the same tokens.json file via a separate TokenStore instance.
    /// </summary>
    public void Reload()
    {
        _tokens = Load();
    }

    public void Remove(string profileName)
    {
        _tokens.Remove(profileName);
        Persist();
    }

    private Dictionary<string, StoredToken> Load()
    {
        if (!File.Exists(_filePath)) return new Dictionary<string, StoredToken>();
        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<Dictionary<string, StoredToken>>(json, JsonOpts)
               ?? new Dictionary<string, StoredToken>();
    }

    private void Persist()
    {
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_tokens, JsonOpts));
    }
}
