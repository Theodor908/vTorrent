// src/vTorrent.CLI/Profiles/ProfileManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace vTorrent.Cli.Profiles;

public record ProfileEntry
{
    public string Host { get; init; } = "";
    public bool Https { get; init; } = true;
    public bool Insecure { get; init; }
    public string Username { get; init; } = "admin";
}

public class ProfileManager
{
    private readonly string _filePath;
    private ProfileData _data;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ProfileManager(string configDir)
    {
        _filePath = Path.Combine(configDir, "profiles.json");
        _data = Load();
    }

    public void Add(string name, string host, bool https, bool insecure, string username)
    {
        _data.Profiles[name] = new ProfileEntry
        {
            Host = host,
            Https = https,
            Insecure = insecure,
            Username = username
        };
        if (string.IsNullOrEmpty(_data.Default))
            _data.Default = name;
        Save();
    }

    public ProfileEntry? Get(string name)
        => _data.Profiles.TryGetValue(name, out var p) ? p : null;

    public string? GetDefault() => _data.Default;

    public void SetDefault(string name) { _data.Default = name; Save(); }

    public void Remove(string name)
    {
        _data.Profiles.Remove(name);
        if (_data.Default == name)
            _data.Default = _data.Profiles.Count > 0 ? _data.Profiles.Keys.First() : null;
        Save();
    }

    public IReadOnlyDictionary<string, ProfileEntry> ListAll() => _data.Profiles;

    public ProfileEntry? ResolveProfile(string? profileName)
    {
        var name = profileName ?? _data.Default;
        return name != null ? Get(name) : null;
    }

    private ProfileData Load()
    {
        if (!File.Exists(_filePath)) return new ProfileData();
        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<ProfileData>(json, JsonOpts) ?? new ProfileData();
    }

    private void Save()
    {
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_data, JsonOpts));
    }

    private class ProfileData
    {
        [JsonPropertyName("default")]
        public string? Default { get; set; }

        [JsonPropertyName("profiles")]
        public Dictionary<string, ProfileEntry> Profiles { get; set; } = new();
    }
}
