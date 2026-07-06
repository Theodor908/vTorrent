using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace vTorrent.Core.PieceIO;

public enum SpaceState { Ok, Warning, Critical }

public sealed class DiskSpaceEvent : EventArgs
{
    public required string DriveRoot { get; init; }
    public required SpaceState State { get; init; }
    public required long FreeBytes { get; init; }
}

public sealed class DiskSpaceFreedEvent : EventArgs
{
    public required string DriveRoot { get; init; }
    public required long FreeBytes { get; init; }
}

internal sealed class DiskSpaceMonitor : IDisposable
{
    private readonly ConcurrentDictionary<string, DriveSpaceInfo> _watchedDrives = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _warningThreshold;
    private readonly long _criticalThreshold;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(30));
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;

    public event EventHandler<DiskSpaceEvent>? SpaceChanged;
    public event EventHandler<DiskSpaceFreedEvent>? SpaceFreed;

    internal DiskSpaceMonitor(long warningThreshold, long criticalThreshold, ILogger logger)
    {
        _warningThreshold = warningThreshold;
        _criticalThreshold = criticalThreshold;
        _logger = logger;
        _ = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void RegisterPath(string savePath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(savePath));
        if (string.IsNullOrEmpty(root)) return;
        _watchedDrives.TryAdd(root, new DriveSpaceInfo { Root = root });
    }

    public SpaceState GetState(string drivePath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(drivePath));
        return root != null && _watchedDrives.TryGetValue(root, out var info)
            ? info.State : SpaceState.Ok;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (await _timer.WaitForNextTickAsync(ct))
        {
            foreach (var (root, info) in _watchedDrives)
            {
                try
                {
                    var drive = new DriveInfo(root);
                    if (!drive.IsReady) continue;

                    var freeBytes = drive.AvailableFreeSpace;
                    var previousState = info.State;

                    info.State = freeBytes switch
                    {
                        _ when freeBytes < _criticalThreshold => SpaceState.Critical,
                        _ when freeBytes < _warningThreshold => SpaceState.Warning,
                        _ => SpaceState.Ok
                    };
                    info.FreeBytes = freeBytes;

                    if (info.State != previousState)
                    {
                        SpaceChanged?.Invoke(this, new DiskSpaceEvent
                        {
                            DriveRoot = root,
                            State = info.State,
                            FreeBytes = freeBytes
                        });

                        // Key innovation: detect space FREED
                        if (previousState is SpaceState.Critical or SpaceState.Warning
                            && info.State == SpaceState.Ok)
                        {
                            SpaceFreed?.Invoke(this, new DiskSpaceFreedEvent
                            {
                                DriveRoot = root,
                                FreeBytes = freeBytes
                            });
                        }

                        _logger.LogInformation("Disk space {Root}: {State} ({Free:N0} bytes free)",
                            root, info.State, freeBytes);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check disk space for {Root}", root);
                }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();
    }

    private sealed class DriveSpaceInfo
    {
        public required string Root { get; init; }
        public SpaceState State { get; set; } = SpaceState.Ok;
        public long FreeBytes { get; set; }
    }
}
