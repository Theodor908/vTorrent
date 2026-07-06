using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using vTorrent.Core.Settings;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Enums;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Enforces seeding ratio and time limits following libtorrent's model.
/// Checks limits periodically and signals when limits are reached.
/// </summary>
public class SeedingLimitEnforcer
{
    private readonly Func<GlobalSettings> _getSettings;
    private readonly ILogger<SeedingLimitEnforcer> _logger;

    // Track which torrents have already had their limits enforced (to avoid repeated actions)
    private readonly HashSet<string> _enforcedTorrents = new();
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new seeding limit enforcer.
    /// </summary>
    /// <param name="getSettings">Function to get current global settings</param>
    /// <param name="logger">Logger instance</param>
    public SeedingLimitEnforcer(Func<GlobalSettings> getSettings, ILogger<SeedingLimitEnforcer> logger)
    {
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Check if a seeding torrent has reached its ratio or time limits.
    /// </summary>
    /// <param name="torrent">The torrent to check</param>
    /// <returns>Result indicating if a limit was reached and what action to take</returns>
    public SeedingLimitResult CheckLimits(ManagedTorrent torrent)
    {
        if (torrent == null)
            return SeedingLimitResult.NoLimit;

        // Only check seeding torrents
        if (torrent.GetStatus().Phase != TransferPhase.Seeding)
            return SeedingLimitResult.NoLimit;

        // Check if we already enforced limits for this torrent
        lock (_lock)
        {
            if (_enforcedTorrents.Contains(torrent.InfoHash))
                return SeedingLimitResult.NoLimit;
        }

        // Get effective limits (per-torrent settings with global fallback)
        var limits = GetEffectiveLimits(torrent.InfoHash);

        // Check ratio limit first (typically more important)
        if (limits.RatioLimit > 0)
        {
            var currentRatio = CalculateRatio(torrent);
            if (currentRatio >= limits.RatioLimit)
            {
                MarkEnforced(torrent.InfoHash);
                var action = DetermineAction(limits);
                _logger.LogInformation(
                    "Torrent '{Name}' reached ratio limit: {Current:F2} >= {Limit:F2}, action: {Action}",
                    torrent.Name, currentRatio, limits.RatioLimit, action);
                return new SeedingLimitResult(
                    LimitReached: true,
                    Type: SeedingLimitType.Ratio,
                    Action: action,
                    CurrentValue: currentRatio,
                    LimitValue: limits.RatioLimit,
                    TorrentName: torrent.Name);
            }
        }

        // Check time limit
        if (limits.TimeLimitMinutes > 0)
        {
            var seedingMinutes = torrent.Statistics.SeedingDuration.TotalMinutes;
            if (seedingMinutes >= limits.TimeLimitMinutes)
            {
                MarkEnforced(torrent.InfoHash);
                var action = DetermineAction(limits);
                _logger.LogInformation(
                    "Torrent '{Name}' reached time limit: {Current:F1} min >= {Limit} min, action: {Action}",
                    torrent.Name, seedingMinutes, limits.TimeLimitMinutes, action);
                return new SeedingLimitResult(
                    LimitReached: true,
                    Type: SeedingLimitType.Time,
                    Action: action,
                    CurrentValue: seedingMinutes,
                    LimitValue: limits.TimeLimitMinutes,
                    TorrentName: torrent.Name);
            }
        }

        // Check seed time ratio limit
        if (limits.SeedTimeRatioLimit > 0 && torrent.Statistics.CompletedTime.HasValue)
        {
            var downloadDuration = torrent.Statistics.CompletedTime.Value - torrent.Statistics.AddedTime;
            if (downloadDuration.TotalSeconds > 0)
            {
                var seedingDuration = torrent.Statistics.SeedingDuration;
                var limitDuration = downloadDuration * limits.SeedTimeRatioLimit;

                if (seedingDuration >= limitDuration)
                {
                    MarkEnforced(torrent.InfoHash);
                    var action = DetermineAction(limits);
                    _logger.LogInformation(
                        "Torrent '{Name}' reached seed time ratio limit: seeded {SeedTime:F1}h >= {DownloadTime:F1}h * {Ratio:F1}, action: {Action}",
                        torrent.Name, seedingDuration.TotalHours, downloadDuration.TotalHours, limits.SeedTimeRatioLimit, action);
                    return new SeedingLimitResult(
                        LimitReached: true,
                        Type: SeedingLimitType.SeedTimeRatio,
                        Action: action,
                        CurrentValue: seedingDuration.TotalHours,
                        LimitValue: downloadDuration.TotalHours * limits.SeedTimeRatioLimit,
                        TorrentName: torrent.Name);
                }
            }
        }

        return SeedingLimitResult.NoLimit;
    }

    /// <summary>
    /// Get effective seeding limits for a torrent (uses global settings).
    /// </summary>
    private EffectiveSeedingLimits GetEffectiveLimits(string infoHash)
    {
        var global = _getSettings();

        return new EffectiveSeedingLimits
        {
            RatioLimit = global.Behavior.SeedRatioLimit,
            TimeLimitMinutes = global.Behavior.SeedTimeLimit,
            SeedTimeRatioLimit = global.Behavior.SeedTimeRatioLimit,
            PauseWhenComplete = global.Behavior.PauseOnSeedComplete,
            StopWhenComplete = global.Behavior.RemoveOnSeedComplete
        };
    }

    /// <summary>
    /// Calculate the share ratio for a torrent.
    /// </summary>
    private static double CalculateRatio(ManagedTorrent torrent)
    {
        var downloaded = torrent.Statistics.AllTimePayloadDownloaded;
        var uploaded = torrent.Statistics.AllTimePayloadUploaded;

        if (downloaded == 0)
        {
            // If nothing downloaded, any upload gives infinite ratio
            // Return a large number but not infinity to avoid issues
            return uploaded > 0 ? 1000.0 : 0.0;
        }

        return (double)uploaded / downloaded;
    }

    /// <summary>
    /// Determine what action to take when a limit is reached.
    /// </summary>
    private static SeedingLimitAction DetermineAction(EffectiveSeedingLimits limits)
    {
        // Stop (remove) takes precedence over pause
        if (limits.StopWhenComplete)
            return SeedingLimitAction.Remove;

        if (limits.PauseWhenComplete)
            return SeedingLimitAction.Pause;

        // No action configured - just report the limit was reached
        return SeedingLimitAction.None;
    }

    /// <summary>
    /// Mark a torrent as having had its limits enforced.
    /// </summary>
    private void MarkEnforced(string infoHash)
    {
        lock (_lock)
        {
            _enforcedTorrents.Add(infoHash);
        }
    }

    /// <summary>
    /// Clear the enforced status for a torrent (e.g., when it's restarted).
    /// </summary>
    public void ClearEnforced(string infoHash)
    {
        lock (_lock)
        {
            _enforcedTorrents.Remove(infoHash);
        }
    }

    /// <summary>
    /// Clear all enforced statuses.
    /// </summary>
    public void ClearAllEnforced()
    {
        lock (_lock)
        {
            _enforcedTorrents.Clear();
        }
    }
}

/// <summary>
/// Type of seeding limit that was reached.
/// </summary>
public enum SeedingLimitType
{
    /// <summary>Share ratio limit (uploaded/downloaded)</summary>
    Ratio,
    /// <summary>Seeding time limit</summary>
    Time,
    /// <summary>Seed time ratio limit (seed time / download time)</summary>
    SeedTimeRatio
}

/// <summary>
/// Action to take when a seeding limit is reached.
/// </summary>
public enum SeedingLimitAction
{
    /// <summary>No action (just report)</summary>
    None,
    /// <summary>Pause the torrent</summary>
    Pause,
    /// <summary>Remove the torrent from the list</summary>
    Remove
}

/// <summary>
/// Result of checking seeding limits for a torrent.
/// </summary>
public record SeedingLimitResult(
    bool LimitReached,
    SeedingLimitType? Type,
    SeedingLimitAction Action,
    double CurrentValue,
    double LimitValue,
    string? TorrentName = null)
{
    /// <summary>
    /// Singleton for when no limit is reached.
    /// </summary>
    public static readonly SeedingLimitResult NoLimit = new(
        LimitReached: false,
        Type: null,
        Action: SeedingLimitAction.None,
        CurrentValue: 0,
        LimitValue: 0);
}

/// <summary>
/// Effective seeding limits after merging per-torrent and global settings.
/// </summary>
public class EffectiveSeedingLimits
{
    /// <summary>Share ratio limit (0 = unlimited)</summary>
    public float RatioLimit { get; set; }

    /// <summary>Time limit in minutes (0 = unlimited)</summary>
    public int TimeLimitMinutes { get; set; }

    /// <summary>Seed time ratio limit: seed time / download time (0 = unlimited)</summary>
    public float SeedTimeRatioLimit { get; set; }

    /// <summary>Pause torrent when limit reached</summary>
    public bool PauseWhenComplete { get; set; }

    /// <summary>Remove torrent when limit reached</summary>
    public bool StopWhenComplete { get; set; }
}

/// <summary>
/// Event args for when a seeding limit is reached.
/// </summary>
public class SeedingLimitReachedEventArgs : EventArgs
{
    public string InfoHash { get; }
    public string TorrentName { get; }
    public SeedingLimitType LimitType { get; }
    public SeedingLimitAction Action { get; }
    public double CurrentValue { get; }
    public double LimitValue { get; }

    public SeedingLimitReachedEventArgs(
        string infoHash,
        string torrentName,
        SeedingLimitType limitType,
        SeedingLimitAction action,
        double currentValue,
        double limitValue)
    {
        InfoHash = infoHash;
        TorrentName = torrentName;
        LimitType = limitType;
        Action = action;
        CurrentValue = currentValue;
        LimitValue = limitValue;
    }
}
