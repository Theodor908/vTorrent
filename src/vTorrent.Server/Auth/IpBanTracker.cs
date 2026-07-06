using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Server.Auth;

public class IpBanTracker
{
    private readonly ConcurrentDictionary<IPAddress, BanEntry> _entries = new();
    private readonly IOptionsMonitor<ServerSettings> _serverMonitor;
    private readonly ILogger<IpBanTracker> _logger;

    public IpBanTracker(IOptionsMonitor<ServerSettings> serverMonitor, ILogger<IpBanTracker> logger)
    {
        _serverMonitor = serverMonitor;
        _logger = logger;
    }

    public bool IsBanned(IPAddress ip)
    {
        if (!_entries.TryGetValue(ip, out var entry))
            return false;
        if (entry.BannedUntil.HasValue && entry.BannedUntil.Value > DateTime.UtcNow)
            return true;
        if (entry.BannedUntil.HasValue)
            _entries.TryRemove(ip, out _);
        return false;
    }

    public TimeSpan? GetRemainingBan(IPAddress ip)
    {
        if (_entries.TryGetValue(ip, out var entry) && entry.BannedUntil.HasValue)
        {
            var remaining = entry.BannedUntil.Value - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : null;
        }
        return null;
    }

    public void RecordFailure(IPAddress ip)
    {
        var settings = _serverMonitor.CurrentValue;
        var entry = _entries.GetOrAdd(ip, _ => new BanEntry());
        var count = entry.IncrementFail();

        if (count >= settings.MaxAuthFailCount && Interlocked.CompareExchange(ref entry.BanFlag, 1, 0) == 0)
        {
            entry.BannedUntil = DateTime.UtcNow.AddSeconds(settings.AuthBanDurationSeconds);
            _logger.LogWarning("IP {Ip} banned for {Duration}s after {Count} failed auth attempts",
                ip, settings.AuthBanDurationSeconds, count);
        }
    }

    public void RecordSuccess(IPAddress ip)
    {
        _entries.TryRemove(ip, out _);
    }

    private class BanEntry
    {
        private int _failCount;
        public int FailCount => _failCount;
        public int IncrementFail() => Interlocked.Increment(ref _failCount);
        public int BanFlag; // 0 = not banned, 1 = banned (set atomically)
        public DateTime? BannedUntil { get; set; }
    }
}
