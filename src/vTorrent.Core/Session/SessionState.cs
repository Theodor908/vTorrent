using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace vTorrent.Core.Session;

/// <summary>
/// Persists session-level state that should survive restarts.
/// Includes IP filter rules, external IPs, port mapping, and persistent stats.
/// DHT state is now stored in SQLite via DhtStatePersistence.
/// </summary>
public class SessionState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Version for migration support
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// When this state was last saved (Unix timestamp)
    /// </summary>
    public long LastSavedTimestamp { get; set; }

    /// <summary>
    /// Alias for LastSavedTimestamp for convenience
    /// </summary>
    public long LastSaved
    {
        get => LastSavedTimestamp;
        set => LastSavedTimestamp = value;
    }

    /// <summary>
    /// Last known listen port
    /// </summary>
    public int ListenPort { get; set; }

    /// <summary>
    /// IP filter rules (banned IPs, allowed ranges, etc.)
    /// </summary>
    public IpFilterState IpFilter { get; set; } = new();

    /// <summary>
    /// External IP addresses discovered via various methods
    /// </summary>
    public List<ExternalIpRecord> ExternalIps { get; set; } = new();

    /// <summary>
    /// Port mapping state (UPnP/NAT-PMP)
    /// </summary>
    public PortMappingState? PortMapping { get; set; }

    /// <summary>
    /// Session statistics that persist across restarts
    /// </summary>
    public PersistentSessionStats PersistentStats { get; set; } = new();

    /// <summary>
    /// Load session state from file
    /// </summary>
    public static async Task<SessionState> LoadAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new SessionState();
            }

            var json = await File.ReadAllTextAsync(path);
            var state = JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
            return state ?? new SessionState();
        }
        catch
        {
            // Return fresh state on any error
            return new SessionState();
        }
    }

    /// <summary>
    /// Save session state to file
    /// </summary>
    public async Task SaveAsync(string path)
    {
        LastSavedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var json = JsonSerializer.Serialize(this, JsonOptions);

        // Atomic write
        var tempPath = path + ".tmp";
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Add an external IP record
    /// </summary>
    public void AddExternalIp(string ip, string source)
    {
        var existing = ExternalIps.Find(e => e.Ip == ip);
        if (existing != null)
        {
            existing.LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            existing.VoteCount++;
        }
        else
        {
            ExternalIps.Add(new ExternalIpRecord
            {
                Ip = ip,
                Source = source,
                FirstSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                VoteCount = 1
            });
        }
    }

    /// <summary>
    /// Get the most likely external IP based on votes
    /// </summary>
    public string? GetExternalIp()
    {
        if (ExternalIps.Count == 0) return null;

        ExternalIpRecord? best = null;
        foreach (var record in ExternalIps)
        {
            if (best == null
                || record.VoteCount > best.VoteCount
                || (record.VoteCount == best.VoteCount && record.LastSeen > best.LastSeen))
            {
                best = record;
            }
        }
        return best?.Ip;
    }
}

/// <summary>
/// IP filter state
/// </summary>
public class IpFilterState
{
    /// <summary>
    /// Banned IP addresses
    /// </summary>
    public List<BannedIpEntry> BannedIps { get; set; } = new();

    /// <summary>
    /// Allowed IP ranges (CIDR notation)
    /// </summary>
    public List<string> AllowedRanges { get; set; } = new();

    /// <summary>
    /// Blocked IP ranges (CIDR notation)
    /// </summary>
    public List<string> BlockedRanges { get; set; } = new();

    /// <summary>
    /// Country codes to block (if GeoIP enabled)
    /// </summary>
    public List<string> BlockedCountries { get; set; } = new();

    /// <summary>
    /// Check if an IP is banned
    /// </summary>
    public bool IsBanned(string ip)
    {
        return BannedIps.Exists(b => b.Ip == ip);
    }

    /// <summary>
    /// Ban an IP address
    /// </summary>
    public void BanIp(string ip, string? reason = null)
    {
        if (!IsBanned(ip))
        {
            BannedIps.Add(new BannedIpEntry
            {
                Ip = ip,
                Reason = reason,
                BannedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }
    }

    /// <summary>
    /// Unban an IP address
    /// </summary>
    public void UnbanIp(string ip)
    {
        BannedIps.RemoveAll(b => b.Ip == ip);
    }
}

/// <summary>
/// Banned IP entry
/// </summary>
public class BannedIpEntry
{
    public string Ip { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public long BannedAt { get; set; }
}

/// <summary>
/// External IP record from various discovery methods
/// </summary>
public class ExternalIpRecord
{
    public string Ip { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public long FirstSeen { get; set; }
    public long LastSeen { get; set; }
    public int VoteCount { get; set; }
}

/// <summary>
/// Port mapping state (UPnP/NAT-PMP)
/// </summary>
public class PortMappingState
{
    public bool UpnpEnabled { get; set; }
    public bool NatPmpEnabled { get; set; }
    public int? MappedTcpPort { get; set; }
    public int? MappedUdpPort { get; set; }
    public string? GatewayIp { get; set; }
    public long? MappingExpires { get; set; }
}

/// <summary>
/// Session statistics that persist across restarts
/// </summary>
public class PersistentSessionStats
{
    /// <summary>
    /// Total bytes downloaded across all sessions
    /// </summary>
    public long TotalBytesDownloaded { get; set; }

    /// <summary>
    /// Total bytes uploaded across all sessions
    /// </summary>
    public long TotalBytesUploaded { get; set; }

    /// <summary>
    /// Total time the client has been running (seconds)
    /// </summary>
    public long TotalRunTimeSeconds { get; set; }

    /// <summary>
    /// Number of times the client has been started
    /// </summary>
    public int SessionCount { get; set; }

    /// <summary>
    /// When the client was first installed/run
    /// </summary>
    public long FirstRunTimestamp { get; set; }

    /// <summary>
    /// Total number of torrents ever added
    /// </summary>
    public int TotalTorrentsAdded { get; set; }

    /// <summary>
    /// Total number of torrents completed
    /// </summary>
    public int TotalTorrentsCompleted { get; set; }
}
