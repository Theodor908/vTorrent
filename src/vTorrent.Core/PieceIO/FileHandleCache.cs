using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace vTorrent.Core.PieceIO
{
    /// <summary>
    /// Generic LRU file handle cache that replaces FileHandlePool.
    /// Supports any handle type (SafeFileHandle, MmapFileEntry, etc.)
    /// via a pluggable factory delegate.
    /// </summary>
    internal sealed class FileHandleCache<THandle> : IAsyncDisposable where THandle : IDisposable
    {
        // ------------------------------------------------------------------ //
        //  Inner types
        // ------------------------------------------------------------------ //

        internal sealed class CacheEntry
        {
            public THandle Handle = default!;
            public FileAccess Access;
            public int RefCount;          // Interlocked
            public long LastAccessTicks;  // Environment.TickCount64 — LRU + idle timeout
            public long CreatedTicks;     // Environment.TickCount64 — close interval (oldest = lowest)
        }

        // ------------------------------------------------------------------ //
        //  Fields
        // ------------------------------------------------------------------ //

        private readonly ConcurrentDictionary<string, CacheEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly SemaphoreSlim _createLock = new(1, 1);
        private readonly SemaphoreSlim _closeAllLock = new(1, 1);
        private readonly Func<string, object?, THandle> _handleFactory;
        private int _maxHandles;
        private readonly int _closeIntervalSeconds;
        private readonly ILogger? _logger;

        private readonly CancellationTokenSource _cts = new();
        private volatile bool _draining;
        private bool _disposed;

        // ------------------------------------------------------------------ //
        //  Constructor
        // ------------------------------------------------------------------ //

        /// <param name="handleFactory">
        ///   Called to open/create a new handle.
        ///   Parameters: (normalizedFilePath, createArg).
        /// </param>
        /// <param name="maxHandles">Maximum open handles before LRU eviction.</param>
        /// <param name="closeIntervalSeconds">
        ///   When &gt; 0 a background timer fires at this interval and closes
        ///   the oldest idle handle.  0 = disabled.
        /// </param>
        public FileHandleCache(
            Func<string, object?, THandle> handleFactory,
            int maxHandles = 40,
            int closeIntervalSeconds = 0,
            ILogger? logger = null)
        {
            _handleFactory = handleFactory ?? throw new ArgumentNullException(nameof(handleFactory));
            _maxHandles = maxHandles > 0 ? maxHandles : throw new ArgumentOutOfRangeException(nameof(maxHandles));
            _closeIntervalSeconds = closeIntervalSeconds;
            _logger = logger;

            // Idle-cleanup timer: every 30 s, evict handles idle > 60 s.
            _ = RunIdleCleanupAsync(_cts.Token);

            // Close-interval timer (optional).
            if (_closeIntervalSeconds > 0)
                _ = RunCloseIntervalAsync(_cts.Token);
        }

        // ------------------------------------------------------------------ //
        //  Public API
        // ------------------------------------------------------------------ //

        /// <summary>Number of cached handles currently tracked.</summary>
        public int Count => _entries.Count;

        /// <summary>Update the max handles cap. Evicts excess handles under semaphore.</summary>
        public void UpdateMaxHandles(int newMax)
        {
            if (_disposed) return;
            try
            {
                _createLock.Wait();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            try
            {
                _maxHandles = Math.Max(1, newMax);
                while (_entries.Count > _maxHandles)
                {
                    EvictLru();
                }
            }
            finally
            {
                _createLock.Release();
            }
        }

        /// <summary>
        /// Synchronous fast-path acquisition. Increments the RefCount of an
        /// existing compatible entry or creates a new one via the factory.
        /// </summary>
        public THandle Acquire(string filePath, FileAccess access, object? createArg = null)
        {
            if (_draining)
                throw new InvalidOperationException("FileHandleCache is draining.");

            var key = Normalize(filePath);

            // Fast path: entry exists and access is compatible.
            if (_entries.TryGetValue(key, out var existing) &&
                IsAccessCompatible(existing.Access, access))
            {
                Interlocked.Increment(ref existing.RefCount);
                Interlocked.Exchange(ref existing.LastAccessTicks, Environment.TickCount64);
                return existing.Handle;
            }

            // Slow path: create with lock.
            _createLock.Wait();
            try
            {
                return CreateOrUpgrade(key, access, createArg);
            }
            finally
            {
                _createLock.Release();
            }
        }

        /// <summary>
        /// Asynchronous acquisition. Prefer this on hot paths to avoid blocking
        /// a thread pool thread while waiting for <see cref="_createLock"/>.
        /// </summary>
        public async ValueTask<THandle> AcquireAsync(
            string filePath,
            FileAccess access,
            object? createArg,
            CancellationToken ct)
        {
            if (_draining)
                throw new InvalidOperationException("FileHandleCache is draining.");

            var key = Normalize(filePath);

            // Fast path.
            if (_entries.TryGetValue(key, out var existing) &&
                IsAccessCompatible(existing.Access, access))
            {
                Interlocked.Increment(ref existing.RefCount);
                Interlocked.Exchange(ref existing.LastAccessTicks, Environment.TickCount64);
                return existing.Handle;
            }

            // Slow path.
            await _createLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return CreateOrUpgrade(key, access, createArg);
            }
            finally
            {
                _createLock.Release();
            }
        }

        /// <summary>
        /// Peek at an entry without touching RefCount.
        /// Returns null when the entry does not exist.
        /// </summary>
        public CacheEntry? TryGet(string filePath)
        {
            var key = Normalize(filePath);
            _entries.TryGetValue(key, out var entry);
            return entry;
        }

        /// <summary>Decrements the RefCount for the given file path.</summary>
        public void Release(string filePath)
        {
            var key = Normalize(filePath);
            if (_entries.TryGetValue(key, out var entry))
                Interlocked.Decrement(ref entry.RefCount);
        }

        /// <summary>
        /// Closes and removes the handle for a specific file.
        /// Waits until the handle's RefCount reaches zero (30 s deadline).
        /// </summary>
        public async ValueTask CloseFileAsync(string filePath)
        {
            var key = Normalize(filePath);
            if (!_entries.TryGetValue(key, out var entry))
                return;

            var deadline = Environment.TickCount64 + 30_000L;
            while (Volatile.Read(ref entry.RefCount) > 0 &&
                   Environment.TickCount64 < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }

            if (_entries.TryRemove(key, out var removed))
                DisposeEntry(removed);
        }

        /// <summary>
        /// Drains all handles. Sets a draining flag (new Acquire calls throw),
        /// waits up to 30 s for all RefCounts to reach zero, then disposes.
        /// </summary>
        public async ValueTask CloseAllAsync(CancellationToken ct)
        {
            await _closeAllLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _draining = true;
                try
                {
                    var deadline = Environment.TickCount64 + 30_000L;
                    bool anyInUse;
                    do
                    {
                        anyInUse = false;
                        foreach (var kvp in _entries)
                        {
                            if (Volatile.Read(ref kvp.Value.RefCount) > 0)
                            {
                                anyInUse = true;
                                break;
                            }
                        }

                        if (anyInUse)
                            await Task.Delay(50, ct).ConfigureAwait(false);

                    } while (anyInUse && Environment.TickCount64 < deadline && !ct.IsCancellationRequested);

                    await _createLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        foreach (var kvp in _entries)
                            DisposeEntry(kvp.Value);
                        _entries.Clear();
                    }
                    finally
                    {
                        _createLock.Release();
                    }
                }
                finally
                {
                    _draining = false;
                }
            }
            finally
            {
                _closeAllLock.Release();
            }
        }

        // ------------------------------------------------------------------ //
        //  IAsyncDisposable
        // ------------------------------------------------------------------ //

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();

            // Give background tasks a moment to observe the cancellation.
            await Task.Yield();

            foreach (var kvp in _entries)
                DisposeEntry(kvp.Value);
            _entries.Clear();

            _createLock.Dispose();
            _closeAllLock.Dispose();
            _cts.Dispose();
        }

        // ------------------------------------------------------------------ //
        //  Private helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Called under <see cref="_createLock"/>. Double-checks the dictionary,
        /// evicts if needed, and creates the entry via the factory.
        /// </summary>
        private THandle CreateOrUpgrade(string key, FileAccess access, object? createArg)
        {
            if (_draining)
                throw new InvalidOperationException("FileHandleCache is draining.");

            // Double-check.
            if (_entries.TryGetValue(key, out var existing) &&
                IsAccessCompatible(existing.Access, access))
            {
                Interlocked.Increment(ref existing.RefCount);
                Interlocked.Exchange(ref existing.LastAccessTicks, Environment.TickCount64);
                return existing.Handle;
            }

            // If an incompatible entry exists (e.g. upgrading Read -> ReadWrite),
            // close the old one first (only if idle).
            if (_entries.TryGetValue(key, out var stale) &&
                Volatile.Read(ref stale.RefCount) == 0)
            {
                if (_entries.TryRemove(key, out var removed))
                    DisposeEntry(removed);
            }

            // Evict LRU entry when at capacity.
            if (_entries.Count >= _maxHandles)
                EvictLru();

            var now = Environment.TickCount64;
            var handle = _handleFactory(key, createArg);

            _logger?.LogDebug(
                "FileHandleCache: opened handle for '{Path}' (access={Access})", key, access);

            var entry = new CacheEntry
            {
                Handle = handle,
                Access = access,
                RefCount = 1,
                LastAccessTicks = now,
                CreatedTicks = now
            };

            _entries[key] = entry;
            return handle;
        }

        /// <summary>
        /// Evicts the idle entry with the oldest <see cref="CacheEntry.LastAccessTicks"/>.
        /// Must be called under <see cref="_createLock"/>.
        /// </summary>
        private void EvictLru()
        {
            string? evictKey = null;
            long oldest = long.MaxValue;

            foreach (var kvp in _entries)
            {
                if (Volatile.Read(ref kvp.Value.RefCount) == 0 &&
                    kvp.Value.LastAccessTicks < oldest)
                {
                    oldest = kvp.Value.LastAccessTicks;
                    evictKey = kvp.Key;
                }
            }

            if (evictKey is null) return;

            if (_entries.TryRemove(evictKey, out var evicted))
            {
                // Re-check after removal to guard against a concurrent Acquire.
                if (Volatile.Read(ref evicted.RefCount) > 0)
                {
                    // Someone grabbed it; put it back.
                    _entries.TryAdd(evictKey, evicted);
                }
                else
                {
                    _logger?.LogDebug(
                        "FileHandleCache: evicted LRU handle for '{Path}'", evictKey);
                    DisposeEntry(evicted);
                }
            }
        }

        /// <summary>Background loop: close handles idle longer than 60 s.</summary>
        private async Task RunIdleCleanupAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    if (_disposed) return;

                    long cutoff = Environment.TickCount64 - 60_000L;

                    foreach (var kvp in _entries)
                    {
                        if (Volatile.Read(ref kvp.Value.RefCount) == 0 &&
                            kvp.Value.LastAccessTicks < cutoff)
                        {
                            if (_entries.TryRemove(kvp.Key, out var removed))
                            {
                                if (Volatile.Read(ref removed.RefCount) > 0)
                                    _entries.TryAdd(kvp.Key, removed);
                                else
                                    DisposeEntry(removed);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        }

        /// <summary>
        /// Background loop: close the oldest idle handle at the configured interval.
        /// </summary>
        private async Task RunCloseIntervalAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_closeIntervalSeconds));
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    if (_disposed) return;

                    // Find idle entry with smallest CreatedTicks.
                    string? oldestKey = null;
                    long oldestCreated = long.MaxValue;

                    foreach (var kvp in _entries)
                    {
                        if (Volatile.Read(ref kvp.Value.RefCount) == 0 &&
                            kvp.Value.CreatedTicks < oldestCreated)
                        {
                            oldestCreated = kvp.Value.CreatedTicks;
                            oldestKey = kvp.Key;
                        }
                    }

                    if (oldestKey is null) continue;

                    if (_entries.TryRemove(oldestKey, out var removed))
                    {
                        if (Volatile.Read(ref removed.RefCount) > 0)
                            _entries.TryAdd(oldestKey, removed);
                        else
                        {
                            _logger?.LogDebug(
                                "FileHandleCache: close-interval evicted handle for '{Path}'",
                                oldestKey);
                            DisposeEntry(removed);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        }

        private static void DisposeEntry(CacheEntry entry)
        {
            try { entry.Handle?.Dispose(); }
            catch (Exception) { /* best-effort */ }
        }

        /// <summary>
        /// Returns true when a cached <paramref name="existing"/> access level
        /// satisfies the <paramref name="requested"/> access.
        /// ReadWrite satisfies both Read and Write requests.
        /// </summary>
        private static bool IsAccessCompatible(FileAccess existing, FileAccess requested) =>
            existing == requested || existing == FileAccess.ReadWrite;

        private static string Normalize(string filePath) => Path.GetFullPath(filePath);
    }
}
