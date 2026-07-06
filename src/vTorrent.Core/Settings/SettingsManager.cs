using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Core.Settings;

/// <summary>
/// Manages loading and saving of global and per-torrent settings.
/// Uses JSON format for human-readable configuration.
/// </summary>
public class SettingsManager
{
    private readonly string _settingsDirectory;
    private readonly string _globalSettingsPath;
    private readonly string _torrentSettingsDirectory;
    private readonly ILogger<SettingsManager> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    private GlobalSettings _globalSettings;
    private bool _isDirty;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    // Settings monitors for live change notification
    private readonly List<Action<GlobalSettings>> _monitorUpdaters = new();

    /// <summary>
    /// Current global settings
    /// </summary>
    public GlobalSettings Current => _globalSettings;

    /// <summary>
    /// Whether settings have unsaved changes
    /// </summary>
    public bool IsDirty => _isDirty;

    public SettingsManager(string dataDirectory, ILogger<SettingsManager> logger)
    {
        _settingsDirectory = Path.Combine(dataDirectory, "settings");
        _globalSettingsPath = Path.Combine(_settingsDirectory, "global.json");
        _torrentSettingsDirectory = Path.Combine(_settingsDirectory, "torrents");
        _logger = logger;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        _globalSettings = new GlobalSettings();

        // Ensure directories exist
        Directory.CreateDirectory(_settingsDirectory);
        Directory.CreateDirectory(_torrentSettingsDirectory);
    }

    /// <summary>
    /// Wire settings monitors for live change notification.
    /// Called by App.axaml.cs after DI container is available.
    /// </summary>
    public void SetMonitors(IServiceProvider serviceProvider)
    {
        _monitorUpdaters.Clear();

        AddMonitor<BehaviorSettings>(serviceProvider, gs => gs.Behavior);
        AddMonitor<BandwidthSettings>(serviceProvider, gs => gs.Bandwidth);
        AddMonitor<QueueSettings>(serviceProvider, gs => gs.Queue);
        AddMonitor<PeerSettings>(serviceProvider, gs => gs.Peer);
        AddMonitor<DiskSettings>(serviceProvider, gs => gs.Disk);
        AddMonitor<ConnectionSettings>(serviceProvider, gs => gs.Connection);
        AddMonitor<TrackerSettings>(serviceProvider, gs => gs.Tracker);
        AddMonitor<DhtSettings>(serviceProvider, gs => gs.Dht);
        AddMonitor<EncryptionSettings>(serviceProvider, gs => gs.Encryption);
        AddMonitor<WebSeedSettings>(serviceProvider, gs => gs.WebSeed);
        AddMonitor<ProxySettings>(serviceProvider, gs => gs.Proxy);
        AddMonitor<VpnSettings>(serviceProvider, gs => gs.Vpn);
        AddMonitor<AutoSaveSettings>(serviceProvider, gs => gs.AutoSave);
        AddMonitor<LoggingSettings>(serviceProvider, gs => gs.Logging);
        AddMonitor<PrivacySettings>(serviceProvider, gs => gs.Privacy);
        AddMonitor<ProtocolSettings>(serviceProvider, gs => gs.Protocol);
        AddMonitor<UISettings>(serviceProvider, gs => gs.UI);
        AddMonitor<I2pSettings>(serviceProvider, gs => gs.I2p);
        AddMonitor<ServerSettings>(serviceProvider, gs => gs.Server);
        AddMonitor<ScheduleSettings>(serviceProvider, gs => gs.Schedule);

        // Push current values to all monitors immediately.
        // LoadAsync() calls NotifyMonitors() before monitors are registered,
        // so we must push again here to ensure monitors have initial values.
        NotifyMonitors();
    }

    private void AddMonitor<T>(IServiceProvider sp, Func<GlobalSettings, T> accessor) where T : class, new()
    {
        var monitor = GetMonitor<T>(sp);
        if (monitor != null)
            _monitorUpdaters.Add(gs => monitor.Update(accessor(gs)));
    }

    private static SettingsMonitor<T>? GetMonitor<T>(IServiceProvider sp) where T : class, new()
    {
        try { return sp.GetService<SettingsMonitor<T>>(); }
        catch { return null; }
    }

    private void NotifyMonitors()
    {
        foreach (var updater in _monitorUpdaters)
        {
            try
            {
                updater(_globalSettings);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Settings monitor callback failed");
            }
        }
    }

    #region Global Settings

    /// <summary>
    /// Load global settings from disk
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(_globalSettingsPath))
            {
                var json = await File.ReadAllTextAsync(_globalSettingsPath);
                var settings = JsonSerializer.Deserialize<GlobalSettings>(json, _jsonOptions);

                if (settings != null)
                {
                    _globalSettings = settings;
                    _logger.LogInformation("Loaded global settings from {Path}", _globalSettingsPath);

                    // Migrate if needed
                    if (settings.Version < GlobalSettings.CurrentVersion)
                    {
                        await MigrateSettingsAsync(settings.Version);
                    }

                    ValidateDiskSettings(_globalSettings.Disk);
                    ValidateTrackerSettings(_globalSettings.Tracker);
                    ValidateConnectionSettings(_globalSettings.Connection);
                    ValidateServerSettings(_globalSettings.Server);
                    ValidatePeerSettings(_globalSettings.Peer);
                    ValidateBehaviorSettings(_globalSettings.Behavior);
                }
                else
                {
                    _logger.LogWarning("Settings file deserialized to null, recreating defaults");
                    _globalSettings = SettingsSeeder.CreateDefaults();
                    await SaveAsync();
                }
            }
            else
            {
                _logger.LogInformation("No settings file found, using defaults");
                _globalSettings = SettingsSeeder.CreateDefaults();
                await SaveAsync();
            }

            NotifyMonitors();
            _isDirty = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings, using defaults");
            _globalSettings = SettingsSeeder.CreateDefaults();
            await SaveAsync();
        }
    }

    /// <summary>
    /// Save global settings to disk
    /// </summary>
    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _globalSettings.UpdatedOn = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(_globalSettings, _jsonOptions);

            // Atomic write
            var tempPath = _globalSettingsPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _globalSettingsPath, overwrite: true);

            _isDirty = false;
            NotifyMonitors();
            _logger.LogDebug("Saved global settings");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save global settings");
            throw;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Update global settings and mark as dirty
    /// </summary>
    public void Update(Action<GlobalSettings> updateAction)
    {
        updateAction(_globalSettings);
        _isDirty = true;
        NotifyMonitors();
    }

    /// <summary>
    /// Update and immediately save
    /// </summary>
    public async Task UpdateAndSaveAsync(Action<GlobalSettings> updateAction)
    {
        Update(updateAction);
        await SaveAsync();
    }

    private GlobalSettings CreateDefaultSettings()
    {
        var settings = new GlobalSettings();

        // Set default save path based on OS
        var downloadsPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        settings.Disk.DefaultSavePath = Path.Combine(downloadsPath, "Downloads");

        return settings;
    }

    private async Task MigrateSettingsAsync(int fromVersion)
    {
        _logger.LogInformation("Migrating settings from v{From} to v{To}", fromVersion, GlobalSettings.CurrentVersion);

        if (fromVersion < 2)
        {
            var json = await File.ReadAllTextAsync(_globalSettingsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("encryption", out var enc))
            {
                bool enabled = enc.TryGetProperty("enabled", out var e) && e.GetBoolean();
                bool require = enc.TryGetProperty("requireEncryption", out var r) && r.GetBoolean();

                if (!enabled)
                {
                    _globalSettings.Encryption.OutPolicy = EncryptionPolicy.Disabled;
                    _globalSettings.Encryption.InPolicy = EncryptionPolicy.Disabled;
                }
                else if (require)
                {
                    _globalSettings.Encryption.OutPolicy = EncryptionPolicy.Forced;
                    _globalSettings.Encryption.InPolicy = EncryptionPolicy.Forced;
                }
                else
                {
                    _globalSettings.Encryption.OutPolicy = EncryptionPolicy.Enabled;
                    _globalSettings.Encryption.InPolicy = EncryptionPolicy.Enabled;
                }

                _globalSettings.Encryption.AllowedLevel = EncryptionLevel.Both;
            }
        }

        if (fromVersion < 4)
        {
            // v3→v4: Settings consolidation.
            // PeerConnectionSettings renamed to PeerSettings — property names unchanged,
            // so System.Text.Json handles this transparently.
            // Removed properties (BlockSize, BucketSize, CheckHashOnCompletion,
            // PreferRc4, EnableUtMetadata) are silently ignored during deserialization.
            // New properties get their defaults from the class definitions.
            _logger.LogInformation("v3→v4: Settings consolidation — structural changes are backward-compatible");
        }

        if (fromVersion < 5)
        {
            // v4→v5: Medium-effort settings (choking algorithms, peer turnover, uTP tuning,
            // mixed mode, piece extent affinity, auto-manage tuning).
            // All new properties have defaults in their class definitions, so
            // System.Text.Json deserialization fills them automatically.
            // No data transformation needed — purely additive.
            _logger.LogInformation("v4→v5: Medium-effort settings added — 21 new properties with libtorrent-parity defaults");
        }

        if (_globalSettings.Version < 6)
        {
            if (_globalSettings.Disk.CloseFileInterval == -1)
            {
                _globalSettings.Disk.CloseFileInterval =
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 240 : 0;
            }
            _logger.LogInformation("Migrated settings from v5 to v6: added disk I/O backend settings");
        }

        if (fromVersion < 7)
        {
            var json = await File.ReadAllTextAsync(_globalSettingsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tracker", out var tracker) &&
                tracker.TryGetProperty("scrapeInterval", out var oldScrape))
            {
                var oldValue = oldScrape.GetInt32();
                if (oldValue != 3600)
                    _globalSettings.Tracker.AutoScrapeInterval = oldValue;
            }
            _logger.LogInformation("v6→v7: Announce timing settings added, scrapeInterval renamed to autoScrapeInterval");
        }

        if (fromVersion < 8)
        {
            _globalSettings.Server.OpenBrowserOnServerStart = false;
            _globalSettings.Server.WebUIBundlePath = "";
            _logger.LogInformation("Migrated v7→v8: Added WebUI desktop integration properties");
        }

        if (fromVersion < 9)
        {
            // v8→v9: Phase 6 libtorrent parity settings (20 new properties).
            // All new properties use C# default initializers.
            // System.Text.Json deserializes missing keys to default values automatically.
            // No data transformation needed.
            _logger.LogInformation("v8→v9: libtorrent parity settings — 20 new properties with libtorrent-matching defaults");
        }

        if (fromVersion < 10)
        {
            // v9→v10: Settings UI redesign — profile system.
            // New properties use C# default initializers.
            // System.Text.Json deserializes missing keys to default values automatically.
            _globalSettings.Version = 10;
        }

        if (fromVersion < 11)
        {
            // v10→v11: Profile scheduler.
            // New Schedule property uses C# default initializer (disabled, all Balanced).
            // System.Text.Json deserializes missing keys to default values automatically.
            // NOTE: Do NOT set _globalSettings.Version here — the terminal line handles it.
            _logger.LogInformation("v10→v11: profile scheduler — Schedule property with defaults");
        }

        _globalSettings.Version = GlobalSettings.CurrentVersion;
        await SaveAsync();
    }

    private void ValidateDiskSettings(DiskSettings d)
    {
        // Enum resets
        if (!Enum.IsDefined(d.BackendType))
        {
            _logger.LogWarning("Setting BackendType value {Value} is not a valid enum, reset to Auto", (int)d.BackendType);
            d.BackendType = DiskBackendType.Auto;
        }
        if (!Enum.IsDefined(d.ReadMode))
        {
            _logger.LogWarning("Setting ReadMode value {Value} is not a valid enum, reset to EnableOsCache", (int)d.ReadMode);
            d.ReadMode = DiskIoMode.EnableOsCache;
        }
        if (!Enum.IsDefined(d.WriteMode))
        {
            _logger.LogWarning("Setting WriteMode value {Value} is not a valid enum, reset to EnableOsCache", (int)d.WriteMode);
            d.WriteMode = DiskIoMode.EnableOsCache;
        }

        // Numeric clamping
        d.MmapFileSizeCutoff = SettingsValidator.Clamp(d.MmapFileSizeCutoff, 1, 10000, nameof(d.MmapFileSizeCutoff), _logger);

        const long mmapCeilingMin = 256L * 1024 * 1024; // 256 MB
        if (d.MmapMemoryCeiling != 0 && d.MmapMemoryCeiling < mmapCeilingMin)
        {
            _logger.LogWarning("Setting MmapMemoryCeiling value {Value} is below minimum 256 MB, clamped", d.MmapMemoryCeiling);
            d.MmapMemoryCeiling = mmapCeilingMin;
        }

        d.CloseFileInterval = SettingsValidator.Clamp(d.CloseFileInterval, -1, 3600, nameof(d.CloseFileInterval), _logger);

        if (d.MaxQueuedDiskBytes != 0 && d.MaxQueuedDiskBytes < 64 * 1024)
        {
            _logger.LogWarning("Setting MaxQueuedDiskBytes value {Value} is below minimum 64 KB, clamped", d.MaxQueuedDiskBytes);
            d.MaxQueuedDiskBytes = 64 * 1024;
        }

        const long diskSpaceMax = 10L * 1024 * 1024 * 1024; // 10 GB
        d.DiskSpaceCriticalBytes = SettingsValidator.Clamp(d.DiskSpaceCriticalBytes, 0L, diskSpaceMax, nameof(d.DiskSpaceCriticalBytes), _logger);

        if (d.DiskSpaceWarningBytes < d.DiskSpaceCriticalBytes)
        {
            _logger.LogWarning("Setting DiskSpaceWarningBytes {Warning} is below DiskSpaceCriticalBytes {Critical}, clamped to critical value",
                d.DiskSpaceWarningBytes, d.DiskSpaceCriticalBytes);
            d.DiskSpaceWarningBytes = d.DiskSpaceCriticalBytes;
        }

        d.OptimisticDiskRetry = SettingsValidator.Clamp(d.OptimisticDiskRetry, 30, 3600, nameof(d.OptimisticDiskRetry), _logger);
        d.MaxDiskRetries = SettingsValidator.Clamp(d.MaxDiskRetries, 0, 100, nameof(d.MaxDiskRetries), _logger);
        d.CheckingMemUsage = SettingsValidator.Clamp(d.CheckingMemUsage, 16, 4096, nameof(d.CheckingMemUsage), _logger);
        d.FilePoolSize = SettingsValidator.Clamp(d.FilePoolSize, 5, 10000, nameof(d.FilePoolSize), _logger);
    }

    private void ValidateBehaviorSettings(BehaviorSettings b)
    {
        b.InitialPickerThreshold = SettingsValidator.Clamp(b.InitialPickerThreshold, 0, 100, nameof(b.InitialPickerThreshold), _logger);
        b.WholePiecesThreshold = SettingsValidator.Clamp(b.WholePiecesThreshold, 1, 120, nameof(b.WholePiecesThreshold), _logger);
        b.UnchokeSlots = SettingsValidator.Clamp(b.UnchokeSlots, 1, 1000, nameof(b.UnchokeSlots), _logger);
    }

    private void ValidateTrackerSettings(TrackerSettings t)
    {
        t.TrackerBackoff = SettingsValidator.Clamp(t.TrackerBackoff, 100, 1000, nameof(t.TrackerBackoff), _logger);
        t.AutoScrapeMinInterval = SettingsValidator.Clamp(t.AutoScrapeMinInterval, 60, 3600, nameof(t.AutoScrapeMinInterval), _logger);
        if (t.AutoScrapeInterval < t.AutoScrapeMinInterval)
        {
            _logger.LogWarning("AutoScrapeInterval {Value} is below AutoScrapeMinInterval {Min}, clamped",
                t.AutoScrapeInterval, t.AutoScrapeMinInterval);
            t.AutoScrapeInterval = t.AutoScrapeMinInterval;
        }
    }

    private void ValidateConnectionSettings(ConnectionSettings c)
    {
        c.LsdAnnounceInterval = SettingsValidator.Clamp(c.LsdAnnounceInterval, 60, 1800, nameof(c.LsdAnnounceInterval), _logger);
        c.ConnectionSpeed = SettingsValidator.Clamp(c.ConnectionSpeed, 1, 500, nameof(c.ConnectionSpeed), _logger);

        if (!c.EnableOutgoingUtp && !c.EnableIncomingUtp && !c.EnableOutgoingTcp && !c.EnableIncomingTcp)
            _logger.LogWarning("All transport protocols disabled — no peer connectivity possible");
    }

    private void ValidatePeerSettings(PeerSettings p)
    {
        p.PeerDscp = SettingsValidator.Clamp(p.PeerDscp, 0, 63, nameof(p.PeerDscp), _logger);
        p.AllowedFastSetSize = SettingsValidator.Clamp(p.AllowedFastSetSize, 0, 100, nameof(p.AllowedFastSetSize), _logger);
        p.MaxRejects = SettingsValidator.Clamp(p.MaxRejects, 1, 1000, nameof(p.MaxRejects), _logger);
        p.RequestQueueTime = SettingsValidator.Clamp(p.RequestQueueTime, 1, 30, nameof(p.RequestQueueTime), _logger);
        p.MaxPeerlistSize = SettingsValidator.Clamp(p.MaxPeerlistSize, 100, 100000, nameof(p.MaxPeerlistSize), _logger);

        const int minMetadata = 1 * 1024 * 1024;    // 1 MB
        const int maxMetadata = 100 * 1024 * 1024;   // 100 MB
        p.MaxMetadataSize = SettingsValidator.Clamp(p.MaxMetadataSize, minMetadata, maxMetadata, nameof(p.MaxMetadataSize), _logger);
    }

    private void ValidateServerSettings(ServerSettings s)
    {
        s.ListenPort = SettingsValidator.Clamp(s.ListenPort, 1024, 65535, nameof(s.ListenPort), _logger);
        s.JwtAccessTokenLifetimeMinutes = SettingsValidator.Clamp(
            s.JwtAccessTokenLifetimeMinutes, 1, 1440, nameof(s.JwtAccessTokenLifetimeMinutes), _logger);
        s.JwtRefreshTokenLifetimeDays = SettingsValidator.Clamp(
            s.JwtRefreshTokenLifetimeDays, 1, 365, nameof(s.JwtRefreshTokenLifetimeDays), _logger);

        if (!string.IsNullOrEmpty(s.JwtSecret) && s.JwtSecret.Length < 32)
        {
            _logger.LogWarning("JwtSecret is too short ({Length} chars, minimum 32). Regenerating.", s.JwtSecret.Length);
            s.JwtSecret = "";
        }

        if (s.ListenAddress == "0.0.0.0" || s.ListenAddress == "::")
            _logger.LogWarning("Server is binding to all interfaces ({Address}). Ensure firewall is configured.", s.ListenAddress);
    }

    #endregion

    #region Per-Torrent Settings

    /// <summary>
    /// Get per-torrent settings, returns null if not found
    /// </summary>
    public async Task<TorrentSettings?> GetTorrentSettingsAsync(string infoHash)
    {
        try
        {
            var path = GetTorrentSettingsPath(infoHash);
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<TorrentSettings>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings for torrent {InfoHash}", infoHash);
            return null;
        }
    }

    /// <summary>
    /// Get effective settings (merged with global)
    /// </summary>
    public async Task<EffectiveTorrentSettings> GetEffectiveSettingsAsync(string infoHash)
    {
        var torrentSettings = await GetTorrentSettingsAsync(infoHash);

        if (torrentSettings != null)
        {
            return torrentSettings.MergeWith(_globalSettings);
        }

        // Return global defaults
        return new EffectiveTorrentSettings
        {
            InfoHash = infoHash,
            MaxConnections = _globalSettings.Connection.MaxConnectionsPerTorrent,
            MaxUploads = _globalSettings.Connection.MaxUploadsPerTorrent,
            UploadLimit = _globalSettings.Bandwidth.PerTorrentUploadLimit,
            DownloadLimit = _globalSettings.Bandwidth.PerTorrentDownloadLimit,
            SequentialDownload = false,
            AutoManaged = true,
            Priority = TorrentPriority.Normal,
            SeedRatioLimit = _globalSettings.Behavior.SeedRatioLimit,
            SeedTimeLimit = _globalSettings.Behavior.SeedTimeLimit,
            StopWhenSeedComplete = _globalSettings.Behavior.RemoveOnSeedComplete,
            PauseWhenSeedComplete = _globalSettings.Behavior.PauseOnSeedComplete,
            SavePath = _globalSettings.Disk.DefaultSavePath
        };
    }

    /// <summary>
    /// Save per-torrent settings
    /// </summary>
    public async Task SaveTorrentSettingsAsync(TorrentSettings settings)
    {
        try
        {
            settings.UpdatedOn = DateTime.UtcNow;
            var path = GetTorrentSettingsPath(settings.InfoHash);
            var json = JsonSerializer.Serialize(settings, _jsonOptions);

            // Atomic write
            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, path, overwrite: true);

            _logger.LogDebug("Saved settings for torrent {InfoHash}", settings.InfoHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings for torrent {InfoHash}", settings.InfoHash);
            throw;
        }
    }

    /// <summary>
    /// Propagate a global setting change to per-torrent settings files.
    /// </summary>
    /// <param name="settingName">Which setting changed (e.g., "MaxConnectionsPerTorrent")</param>
    /// <param name="oldValue">The previous global value (boxed)</param>
    /// <param name="mode">How to propagate</param>
    public async Task PropagateGlobalSettingAsync(string settingName, object oldValue, SettingsPropagationMode mode)
    {
        if (mode == SettingsPropagationMode.None) return;
        if (!Directory.Exists(_torrentSettingsDirectory)) return;

        var files = Directory.GetFiles(_torrentSettingsDirectory, "*.json");
        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var settings = JsonSerializer.Deserialize<TorrentSettings>(json, _jsonOptions);
                if (settings == null) continue;

                bool changed = ResetSettingToSentinel(settings, settingName, oldValue, mode);
                if (changed)
                {
                    settings.UpdatedOn = DateTime.UtcNow;
                    var updatedJson = JsonSerializer.Serialize(settings, _jsonOptions);
                    var tempPath = file + ".tmp";
                    await File.WriteAllTextAsync(tempPath, updatedJson);
                    File.Move(tempPath, file, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to propagate setting to {File}", Path.GetFileName(file));
            }
        }
    }

    /// <summary>
    /// Reset a single per-torrent setting to its sentinel value based on propagation mode.
    /// Returns true if the setting was changed.
    /// </summary>
    private static bool ResetSettingToSentinel(TorrentSettings settings, string settingName, object oldValue, SettingsPropagationMode mode)
    {
        switch (settingName)
        {
            case "MaxConnectionsPerTorrent":
                if (mode == SettingsPropagationMode.OverrideAll ||
                    (mode == SettingsPropagationMode.OnlyMatchingOldDefault && settings.MaxConnections == Convert.ToInt32(oldValue)))
                {
                    if (settings.MaxConnections == -1) return false;
                    settings.MaxConnections = -1;
                    return true;
                }
                return false;

            case "MaxUploadsPerTorrent":
                if (mode == SettingsPropagationMode.OverrideAll ||
                    (mode == SettingsPropagationMode.OnlyMatchingOldDefault && settings.MaxUploads == Convert.ToInt32(oldValue)))
                {
                    if (settings.MaxUploads == -1) return false;
                    settings.MaxUploads = -1;
                    return true;
                }
                return false;

            case "PerTorrentUploadLimit":
                if (mode == SettingsPropagationMode.OverrideAll ||
                    (mode == SettingsPropagationMode.OnlyMatchingOldDefault && settings.UploadLimit == Convert.ToInt32(oldValue)))
                {
                    if (settings.UploadLimit == -1) return false;
                    settings.UploadLimit = -1;
                    return true;
                }
                return false;

            case "PerTorrentDownloadLimit":
                if (mode == SettingsPropagationMode.OverrideAll ||
                    (mode == SettingsPropagationMode.OnlyMatchingOldDefault && settings.DownloadLimit == Convert.ToInt32(oldValue)))
                {
                    if (settings.DownloadLimit == -1) return false;
                    settings.DownloadLimit = -1;
                    return true;
                }
                return false;

            case "SeedRatioLimit":
                if (mode == SettingsPropagationMode.OverrideAll ||
                    (mode == SettingsPropagationMode.OnlyMatchingOldDefault && settings.Seeding.RatioLimit == Convert.ToSingle(oldValue)))
                {
                    if (settings.Seeding.RatioLimit == null) return false;
                    settings.Seeding.RatioLimit = null;
                    return true;
                }
                return false;

            case "SeedTimeLimit":
                if (mode == SettingsPropagationMode.OverrideAll ||
                    (mode == SettingsPropagationMode.OnlyMatchingOldDefault && settings.Seeding.TimeLimitMinutes == Convert.ToInt32(oldValue)))
                {
                    if (settings.Seeding.TimeLimitMinutes == null) return false;
                    settings.Seeding.TimeLimitMinutes = null;
                    return true;
                }
                return false;

            case "RemoveOnSeedComplete":
                if (mode == SettingsPropagationMode.OverrideAll ||
                    (mode == SettingsPropagationMode.OnlyMatchingOldDefault && settings.Seeding.StopWhenComplete == Convert.ToBoolean(oldValue)))
                {
                    if (settings.Seeding.StopWhenComplete == null) return false;
                    settings.Seeding.StopWhenComplete = null;
                    return true;
                }
                return false;

            case "PauseOnSeedComplete":
                if (mode == SettingsPropagationMode.OverrideAll ||
                    (mode == SettingsPropagationMode.OnlyMatchingOldDefault && settings.Seeding.PauseWhenComplete == Convert.ToBoolean(oldValue)))
                {
                    if (settings.Seeding.PauseWhenComplete == null) return false;
                    settings.Seeding.PauseWhenComplete = null;
                    return true;
                }
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Delete per-torrent settings
    /// </summary>
    public Task DeleteTorrentSettingsAsync(string infoHash)
    {
        try
        {
            var path = GetTorrentSettingsPath(infoHash);
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogDebug("Deleted settings for torrent {InfoHash}", infoHash);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete settings for torrent {InfoHash}", infoHash);
        }

        return Task.CompletedTask;
    }

    private string GetTorrentSettingsPath(string infoHash)
    {
        return Path.Combine(_torrentSettingsDirectory, $"{infoHash}.json");
    }

    #endregion

    #region Export/Import

    /// <summary>
    /// Export all settings to a file
    /// </summary>
    public async Task ExportAsync(string filePath)
    {
        var exportData = new SettingsExport
        {
            ExportedOn = DateTime.UtcNow,
            GlobalSettings = _globalSettings,
            TorrentSettings = new System.Collections.Generic.List<TorrentSettings>()
        };

        // Load all torrent settings
        foreach (var file in Directory.GetFiles(_torrentSettingsDirectory, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var settings = JsonSerializer.Deserialize<TorrentSettings>(json, _jsonOptions);
                if (settings != null)
                {
                    exportData.TorrentSettings.Add(settings);
                }
            }
            catch { /* skip invalid files */ }
        }

        var exportJson = JsonSerializer.Serialize(exportData, _jsonOptions);
        await File.WriteAllTextAsync(filePath, exportJson);

        _logger.LogInformation("Exported settings to {Path}", filePath);
    }

    /// <summary>
    /// Import settings from a file
    /// </summary>
    public async Task ImportAsync(string filePath, bool overwrite = false)
    {
        var json = await File.ReadAllTextAsync(filePath);
        var importData = JsonSerializer.Deserialize<SettingsExport>(json, _jsonOptions);

        if (importData?.GlobalSettings != null)
        {
            _globalSettings = importData.GlobalSettings;
            await SaveAsync();
        }

        if (importData?.TorrentSettings != null)
        {
            foreach (var settings in importData.TorrentSettings)
            {
                var existingPath = GetTorrentSettingsPath(settings.InfoHash);
                if (overwrite || !File.Exists(existingPath))
                {
                    await SaveTorrentSettingsAsync(settings);
                }
            }
        }

        _logger.LogInformation("Imported settings from {Path}", filePath);
    }

    #endregion
}

/// <summary>
/// Data structure for settings export/import
/// </summary>
public class SettingsExport
{
    public DateTime ExportedOn { get; set; }
    public GlobalSettings? GlobalSettings { get; set; }
    public System.Collections.Generic.List<TorrentSettings>? TorrentSettings { get; set; }
}
