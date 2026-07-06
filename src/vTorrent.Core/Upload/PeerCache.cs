using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Core.Interfaces;
using vTorrent.Storage;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Records;

namespace vTorrent.Core.Upload;

/// <summary>
/// Persistent cache for peer information across sessions.
/// Saves and loads peer data via SQLite for fast resume.
/// Based on libtorrent's resume_data peer persistence patterns.
/// </summary>
public class PeerCache : IDisposable
{
    private readonly ILogger<PeerCache> _logger;
    private readonly TorrentDatabase _database;
    private bool _disposed;

    /// <summary>
    /// Maximum number of peers to cache per torrent.
    /// </summary>
    public int MaxPeersPerTorrent { get; set; } = 500;

    /// <summary>
    /// Maximum number of banned peers to cache per torrent.
    /// </summary>
    public int MaxBannedPeersPerTorrent { get; set; } = 100;

    public PeerCache(TorrentDatabase database, ILogger<PeerCache> logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger;
    }

    /// <summary>
    /// Saves peer data to SQLite for a torrent.
    /// </summary>
    public async Task SavePeersAsync(
        string infohash,
        IEnumerable<PeerState> peers,
        IEnumerable<PeerState> bannedPeers,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(infohash))
            return;

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var records = peers
                .Where(p => p?.Info != null && p.Score != null)
                .OrderByDescending(p => p.Score.Priority)
                .ThenBy(p => p.Score.FailedConnections)
                .Take(MaxPeersPerTorrent)
                .Select(p => new KnownPeerRecord
                {
                    InfoHash = infohash,
                    Ip = p.Info.IpAddress.ToString(),
                    Port = p.Info.Port,
                    Source = p.Info.Source ?? "unknown",
                    LastSeen = now,
                    LastConnected = p.LastConnectedAt.HasValue
                        ? new DateTimeOffset(p.LastConnectedAt.Value, TimeSpan.Zero).ToUnixTimeSeconds()
                        : null,
                    FailedCount = p.Score.FailedConnections,
                    TrustPoints = p.Score.TrustPoints,
                    TotalDownloaded = p.Score.TotalDownloaded,
                    TotalUploaded = p.Score.TotalUploaded
                })
                .ToList();

            await _database.SaveKnownPeersAsync(infohash, records);

            // Save banned peers as known peers with high fail count so they persist,
            // and also save them via the banned peers table
            foreach (var bp in bannedPeers
                .Where(p => p?.Info != null && p.BannedUntil.HasValue)
                .Take(MaxBannedPeersPerTorrent))
            {
                try
                {
                    var reason = bp.Score?.LastBanReason ?? "unknown";
                    await _database.BanPeerAsync(bp.Info.IpAddress.ToString(), reason);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to save banned peer {Ip}", bp.Info.IpAddress);
                }
            }

            _logger?.LogInformation(
                "Saved {PeerCount} peers for {Infohash} to database",
                records.Count, infohash.Substring(0, Math.Min(8, infohash.Length)));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save peer cache for {Infohash}", infohash);
        }
    }

    /// <summary>
    /// Restores peers from SQLite into the peer registry.
    /// </summary>
    public async Task<int> RestorePeersToRegistryAsync(
        string infohash,
        IPeerRegistry registry,
        CancellationToken cancellationToken = default)
    {
        int restoredCount = 0;

        try
        {
            var knownPeers = await _database.GetKnownPeersForRestoreAsync(infohash, MaxPeersPerTorrent);

            if (knownPeers == null || knownPeers.Count == 0)
            {
                _logger?.LogDebug("No cached peers found for {Infohash}", infohash);
            }
            else
            {
                foreach (var cachedPeer in knownPeers)
                {
                    try
                    {
                        if (!IPAddress.TryParse(cachedPeer.Ip, out var ip))
                            continue;

                        var peerInfo = new PeerInfo(ip, cachedPeer.Port)
                        {
                            Source = "resume_data"
                        };

                        var state = registry.GetOrRegister(peerInfo);

                        if (state == null)
                            continue;

                        if (state.Score != null)
                        {
                            state.Score.FailedConnections = Math.Min(cachedPeer.FailedCount, 31);
                            state.Score.TrustPoints = (sbyte)Math.Clamp(cachedPeer.TrustPoints, -7, 8);
                            state.Score.TotalDownloaded = cachedPeer.TotalDownloaded;
                            state.Score.TotalUploaded = cachedPeer.TotalUploaded;
                            state.Score.Source = "resume_data";
                            state.Score.UpdatePriority();
                        }

                        if (cachedPeer.LastConnected.HasValue)
                        {
                            state.LastConnectedAt = DateTimeOffset
                                .FromUnixTimeSeconds(cachedPeer.LastConnected.Value)
                                .UtcDateTime;
                        }

                        restoredCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Failed to restore peer {Ip}:{Port}",
                            cachedPeer.Ip, cachedPeer.Port);
                    }
                }
            }

            // Restore banned peers
            var bannedPeers = await _database.GetBannedPeersAsync();
            foreach (var bannedPeer in bannedPeers)
            {
                try
                {
                    if (!IPAddress.TryParse(bannedPeer.Ip, out var ip))
                        continue;

                    var peerInfo = new PeerInfo(ip, 0)
                    {
                        Source = "resume_data"
                    };

                    var state = registry.GetOrRegister(peerInfo);
                    if (state == null)
                        continue;

                    var key = registry.GetPeerKey(peerInfo);

                    // Apply a 24-hour ban from the time it was originally banned
                    var bannedAt = DateTimeOffset.FromUnixTimeSeconds(bannedPeer.BannedAt).UtcDateTime;
                    var bannedUntil = bannedAt + TimeSpan.FromHours(24);
                    var remaining = bannedUntil - DateTime.UtcNow;

                    if (remaining > TimeSpan.Zero)
                    {
                        registry.Ban(key, remaining, $"Restored: {bannedPeer.Reason ?? "unknown"}");
                        _logger?.LogDebug("Restored ban for {Ip} (expires in {Time})",
                            bannedPeer.Ip, remaining);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to restore banned peer {Ip}", bannedPeer.Ip);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to restore peers from database for {Infohash}", infohash);
        }

        _logger?.LogInformation("Restored {Count} peers from database for {Infohash}",
            restoredCount, infohash.Substring(0, Math.Min(8, infohash.Length)));

        return restoredCount;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
    }
}
