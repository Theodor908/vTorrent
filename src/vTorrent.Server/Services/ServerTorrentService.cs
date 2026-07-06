using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Settings;
using vTorrent.Server.Models;

namespace vTorrent.Server.Services;

public class ServerTorrentService
{
    private readonly ITorrentService _torrentService;
    private readonly SettingsManager _settingsManager;
    private readonly SettingsRedactor _redactor;
    private readonly ILogger<ServerTorrentService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public ServerTorrentService(
        ITorrentService torrentService,
        SettingsManager settingsManager,
        SettingsRedactor redactor,
        ILogger<ServerTorrentService> logger)
    {
        _torrentService = torrentService;
        _settingsManager = settingsManager;
        _redactor = redactor;
        _logger = logger;
    }

    // --- Torrent queries with filtering/sorting/pagination ---

    public IReadOnlyList<TorrentSnapshot> GetTorrents(
        string? phase = null, string? intent = null, string? health = null,
        int? categoryId = null, string? tag = null,
        string? sort = null, int? limit = null, int? offset = null)
    {
        var all = _torrentService.GetTorrents();
        var filtered = ApplyFilters(all, phase, intent, health, categoryId, tag);
        var sorted = ApplySort(filtered, sort);
        return ApplyPagination(sorted, limit, offset);
    }

    // --- Delegated operations (all pass-through) ---

    public Task<string> AddTorrentAsync(string filePath, AddTorrentRequest request)
        => _torrentService.AddTorrentAsync(filePath, MapOptions(request));

    public Task<string> AddMagnetAsync(AddMagnetRequest request)
        => _torrentService.AddMagnetAsync(request.MagnetUri, MapOptions(request));

    // --- Settings ---

    public JsonObject GetRedactedSettings()
    {
        var settings = _settingsManager.Current;
        var json = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        _redactor.Redact(json);
        return json;
    }

    public async Task UpdateSettingsAsync(JsonObject incoming)
    {
        _redactor.StripRedactedFields(incoming);

        // SettingsManager.UpdateAndSaveAsync takes Action<GlobalSettings> — we mutate the
        // live object in-place. To merge JSON partial updates, we serialize the current
        // settings, overlay the incoming fields, and deserialize back into the live object.
        await _settingsManager.UpdateAndSaveAsync(current =>
        {
            // Serialize current to JSON, overlay incoming fields, deserialize back
            var currentJson = JsonSerializer.SerializeToNode(current, JsonOptions)!.AsObject();

            // Deep merge: for each top-level key in incoming, replace the corresponding
            // key in currentJson (settings are grouped by section: "bandwidth", "disk", etc.)
            foreach (var (key, value) in incoming)
            {
                if (value != null)
                    currentJson[key] = value.DeepClone();
            }

            // Deserialize the merged JSON into a fresh GlobalSettings
            var merged = currentJson.Deserialize<GlobalSettings>(JsonOptions);
            if (merged == null) return;

            // Copy each settings group from merged back to the live current object.
            // GlobalSettings properties are mutable reference types — reassign each.
            current.Connection = merged.Connection;
            current.Bandwidth = merged.Bandwidth;
            current.Protocol = merged.Protocol;
            current.Dht = merged.Dht;
            current.Disk = merged.Disk;
            current.Queue = merged.Queue;
            current.Behavior = merged.Behavior;
            current.Tracker = merged.Tracker;
            current.Peer = merged.Peer;
            current.AutoSave = merged.AutoSave;
            current.Logging = merged.Logging;
            current.Encryption = merged.Encryption;
            current.UI = merged.UI;
            current.WebSeed = merged.WebSeed;
            current.Privacy = merged.Privacy;
            current.Proxy = merged.Proxy;
            current.Vpn = merged.Vpn;
            current.I2p = merged.I2p;
            current.PeerClasses = merged.PeerClasses;
            current.Server = merged.Server;
        });

        await _torrentService.ApplySettingsAsync();
    }

    // --- Static helpers (public for testing) ---

    public static IReadOnlyList<TorrentSnapshot> ApplyFilters(
        IReadOnlyList<TorrentSnapshot> snapshots,
        string? phase = null, string? intent = null, string? health = null,
        int? categoryId = null, string? tag = null)
    {
        IEnumerable<TorrentSnapshot> result = snapshots;

        if (!string.IsNullOrEmpty(phase) && Enum.TryParse<TransferPhase>(phase, ignoreCase: true, out var parsedPhase))
            result = result.Where(s => s.Status.Phase == parsedPhase);

        if (!string.IsNullOrEmpty(intent) && Enum.TryParse<UserIntent>(intent, ignoreCase: true, out var parsedIntent))
            result = result.Where(s => s.Status.Intent == parsedIntent);

        // Health filter: "error" → has Error, "missingfiles" → MissingFiles, "ok" → neither
        if (!string.IsNullOrEmpty(health))
        {
            if (health.Equals("Error", StringComparison.OrdinalIgnoreCase))
                result = result.Where(s => s.Status.Error != null);
            else if (health.Equals("MissingFiles", StringComparison.OrdinalIgnoreCase))
                result = result.Where(s => s.Status.MissingFiles);
            else if (health.Equals("Ok", StringComparison.OrdinalIgnoreCase))
                result = result.Where(s => s.Status.Error == null && !s.Status.MissingFiles);
        }

        if (categoryId.HasValue)
            result = result.Where(s => s.CategoryId == categoryId.Value);

        if (!string.IsNullOrEmpty(tag))
            result = result.Where(s => s.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));

        return result.ToList();
    }

    public static IReadOnlyList<TorrentSnapshot> ApplySort(
        IReadOnlyList<TorrentSnapshot> snapshots, string? sort)
    {
        if (string.IsNullOrEmpty(sort)) return snapshots;

        var parts = sort.Split(':');
        var field = parts[0].ToLowerInvariant();
        var desc = parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);

        IOrderedEnumerable<TorrentSnapshot> ordered = field switch
        {
            "name" => desc ? snapshots.OrderByDescending(s => s.Name) : snapshots.OrderBy(s => s.Name),
            "progress" => desc ? snapshots.OrderByDescending(s => s.VerifiedProgress) : snapshots.OrderBy(s => s.VerifiedProgress),
            "size" => desc ? snapshots.OrderByDescending(s => s.TotalSize) : snapshots.OrderBy(s => s.TotalSize),
            "downloadrate" => desc ? snapshots.OrderByDescending(s => s.PayloadDownloadRate) : snapshots.OrderBy(s => s.PayloadDownloadRate),
            "uploadrate" => desc ? snapshots.OrderByDescending(s => s.PayloadUploadRate) : snapshots.OrderBy(s => s.PayloadUploadRate),
            "addedon" => desc ? snapshots.OrderByDescending(s => s.AddedOn) : snapshots.OrderBy(s => s.AddedOn),
            "queue" => desc ? snapshots.OrderByDescending(s => s.QueuePosition) : snapshots.OrderBy(s => s.QueuePosition),
            _ => snapshots.OrderBy(s => s.Name)
        };

        return ordered.ToList();
    }

    public static IReadOnlyList<TorrentSnapshot> ApplyPagination(
        IReadOnlyList<TorrentSnapshot> snapshots, int? limit, int? offset)
    {
        IEnumerable<TorrentSnapshot> result = snapshots;
        if (offset.HasValue && offset.Value > 0) result = result.Skip(offset.Value);
        if (limit.HasValue && limit.Value > 0) result = result.Take(limit.Value);
        return result.ToList();
    }

    private static TorrentAddOptions MapOptions(AddTorrentRequest r) => new()
    {
        SavePath = r.SavePath,
        StartImmediately = r.StartImmediately,
        SequentialDownload = r.SequentialDownload,
        FirstLastPiecePriority = r.FirstLastPiecePriority,
        AddToTopOfQueue = r.AddToTopOfQueue,
        FilePriorities = r.FilePriorities
    };

    private static TorrentAddOptions MapOptions(AddMagnetRequest r) => new()
    {
        SavePath = r.SavePath,
        StartImmediately = r.StartImmediately,
        SequentialDownload = r.SequentialDownload,
        FirstLastPiecePriority = r.FirstLastPiecePriority,
        AddToTopOfQueue = r.AddToTopOfQueue,
        FilePriorities = r.FilePriorities
    };
}
