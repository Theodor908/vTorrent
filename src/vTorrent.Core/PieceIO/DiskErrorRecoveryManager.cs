using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.PieceIO;

public enum DiskErrorType
{
    SpaceFull,          // IOException HRESULT 0x80070070 (Win) or errno 28 ENOSPC (Linux)
    PermissionDenied,   // UnauthorizedAccessException
    IoError,            // General IOException
    FileNotFound        // FileNotFoundException — file deleted externally
}

internal sealed class DiskErrorEntry
{
    public required string InfoHashHex { get; init; }
    public required DiskErrorType ErrorType { get; init; }
    public required string FilePath { get; init; }
    public required DateTimeOffset FirstErrorTime { get; init; }
    public int RetryCount { get; set; }
    public DateTimeOffset NextRetryTime { get; set; }
}

internal sealed class DiskErrorRecoveryManager : IDisposable
{
    private readonly ConcurrentDictionary<string, DiskErrorEntry> _errored = new();
    private readonly DiskSpaceMonitor _spaceMonitor;
    private readonly DiskSettings _settings;
    private readonly PeriodicTimer _retryTimer = new(TimeSpan.FromSeconds(15));
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;

    // Callback for retrying a torrent — injected by orchestrator
    private readonly Func<string, Task<bool>>? _retryCallback;  // infoHashHex → success

    internal DiskErrorRecoveryManager(
        DiskSpaceMonitor spaceMonitor,
        DiskSettings settings,
        Func<string, Task<bool>>? retryCallback,
        ILogger logger)
    {
        _spaceMonitor = spaceMonitor;
        _settings = settings;
        _retryCallback = retryCallback;
        _logger = logger;

        // Subscribe to SpaceFreed for immediate retry
        _spaceMonitor.SpaceFreed += OnSpaceFreed;

        _ = Task.Run(() => RetryLoopAsync(_cts.Token));
    }

    /// <summary>Called by PieceManager when a write fails.</summary>
    public void OnDiskError(string infoHashHex, Exception exception, string filePath)
    {
        var errorType = Classify(exception);

        // FileNotFound: immediate permanent error, no retry
        if (errorType == DiskErrorType.FileNotFound)
        {
            _logger.LogError("Permanent disk error for {Hash}: file not found {File}", infoHashHex, filePath);
            return;
        }

        var entry = _errored.GetOrAdd(infoHashHex, _ => new DiskErrorEntry
        {
            InfoHashHex = infoHashHex,
            ErrorType = errorType,
            FilePath = filePath,
            FirstErrorTime = DateTimeOffset.UtcNow,
            NextRetryTime = DateTimeOffset.UtcNow + GetNextDelay(0)
        });

        _logger.LogWarning("Disk error for {Hash}: {Type} on {File}, retry #{Attempt} at {NextRetry}",
            infoHashHex, errorType, filePath, entry.RetryCount, entry.NextRetryTime);
    }

    private static DiskErrorType Classify(Exception ex) => ex switch
    {
        IOException io when io.HResult == unchecked((int)0x80070070) => DiskErrorType.SpaceFull,  // Win ERROR_DISK_FULL
        IOException io when io.HResult == 28 => DiskErrorType.SpaceFull,                          // Linux ENOSPC
        IOException io when io.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase) => DiskErrorType.SpaceFull,
        UnauthorizedAccessException => DiskErrorType.PermissionDenied,
        FileNotFoundException => DiskErrorType.FileNotFound,
        DirectoryNotFoundException => DiskErrorType.FileNotFound,
        _ => DiskErrorType.IoError
    };

    private TimeSpan GetNextDelay(int retryCount)
    {
        var baseDelay = TimeSpan.FromSeconds(120);
        var backoff = TimeSpan.FromSeconds(baseDelay.TotalSeconds * Math.Pow(2, Math.Min(retryCount, 4)));
        var cap = TimeSpan.FromSeconds(_settings.OptimisticDiskRetry);
        return backoff < cap ? backoff : cap;
    }

    private async Task RetryLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _retryTimer.WaitForNextTickAsync(ct))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var (hash, entry) in _errored)
                {
                    if (entry.NextRetryTime > now) continue;

                    // PermissionDenied: only 1 retry
                    if (entry.ErrorType == DiskErrorType.PermissionDenied && entry.RetryCount >= 1)
                    {
                        _logger.LogError("Permanent permission error for {Hash} after 1 retry", hash);
                        _errored.TryRemove(hash, out _);
                        continue;
                    }

                    // Max retries exceeded
                    if (_settings.MaxDiskRetries > 0 && entry.RetryCount >= _settings.MaxDiskRetries)
                    {
                        _logger.LogError("Disk retry exhausted for {Hash} after {Count} attempts", hash, entry.RetryCount);
                        _errored.TryRemove(hash, out _);
                        continue;
                    }

                    // Attempt retry
                    var success = false;
                    if (_retryCallback != null)
                    {
                        try { success = await _retryCallback(hash); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Retry callback failed for {Hash}", hash); }
                    }

                    if (success)
                    {
                        _logger.LogInformation("Disk retry succeeded for {Hash}", hash);
                        _errored.TryRemove(hash, out _);
                    }
                    else
                    {
                        entry.RetryCount++;
                        entry.NextRetryTime = now + GetNextDelay(entry.RetryCount);
                        _logger.LogDebug("Disk retry failed for {Hash}, next attempt at {Next}", hash, entry.NextRetryTime);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnSpaceFreed(object? sender, DiskSpaceFreedEvent evt)
    {
        foreach (var (hash, entry) in _errored)
        {
            if (entry.ErrorType == DiskErrorType.SpaceFull)
            {
                entry.NextRetryTime = DateTimeOffset.UtcNow; // Retry on next tick (≤15s)
                _logger.LogInformation("Disk space freed on {Drive}, scheduling immediate retry for {Hash}", evt.DriveRoot, hash);
            }
        }
    }

    public bool IsErrored(string infoHashHex) => _errored.ContainsKey(infoHashHex);

    public DiskErrorEntry? GetError(string infoHashHex) => _errored.TryGetValue(infoHashHex, out var e) ? e : null;

    /// <summary>Manual clear — e.g., user forces recheck.</summary>
    public void ClearError(string infoHashHex) => _errored.TryRemove(infoHashHex, out _);

    public void Dispose()
    {
        _spaceMonitor.SpaceFreed -= OnSpaceFreed;
        _cts.Cancel();
        _retryTimer.Dispose();
        _cts.Dispose();
    }
}
