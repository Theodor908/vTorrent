using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Core.State;

/// <summary>
/// Debounced persistence for torrent state changes. Accumulates dirty flags
/// and flushes on a timer or on demand (graceful shutdown).
/// </summary>
internal class PersistenceQueue : IDisposable
{
    private readonly HashSet<string> _dirty = new();
    private readonly Func<string, Task> _saveFunc;
    private readonly Timer? _flushTimer;

    public PersistenceQueue(Func<string, Task> saveFunc, TimeSpan? autoFlushInterval = null)
    {
        _saveFunc = saveFunc;
        if (autoFlushInterval.HasValue)
        {
            _flushTimer = new Timer(_ => _ = FlushAsync(), null, autoFlushInterval.Value, autoFlushInterval.Value);
        }
    }

    public void MarkDirty(string infoHash)
    {
        lock (_dirty) _dirty.Add(infoHash);
    }

    public async Task FlushAsync()
    {
        HashSet<string> batch;
        lock (_dirty)
        {
            if (_dirty.Count == 0) return;
            batch = new HashSet<string>(_dirty);
            _dirty.Clear();
        }

        var failed = new List<string>();
        await Parallel.ForEachAsync(batch,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (hash, ct) =>
            {
                try { await _saveFunc(hash).ConfigureAwait(false); }
                catch { lock (failed) failed.Add(hash); }
            }).ConfigureAwait(false);

        if (failed.Count > 0)
            lock (_dirty) foreach (var h in failed) _dirty.Add(h);
    }

    public async Task DrainAsync()
    {
        _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        await FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
    }
}
