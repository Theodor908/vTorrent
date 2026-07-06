using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using vTorrent.Core.PieceIO;
using Xunit;

namespace vTorrent.Tests.Unit.PieceIO;

/// <summary>
/// Regression tests for the "FileHandleCache permanently drains on seed transition" bug.
///
/// <see cref="FileHandleCache{THandle}.CloseAllAsync"/> is called transiently (e.g. on the
/// seed transition via PieceManager.ReleaseWriteHandlesAsync, and on a move-storage fence),
/// and both callers expect handles to be lazily reopened on the next I/O. Before the fix,
/// CloseAllAsync sets the internal draining flag and never clears it, so every subsequent
/// Acquire/AcquireAsync call throws "FileHandleCache is draining." forever, silently
/// breaking all disk I/O (and therefore uploads) for any torrent that ever reached 100%.
/// </summary>
public class FileHandleCacheDrainRecoveryTests
{
    /// <summary>Trivial disposable handle used as the cache's THandle for these tests.</summary>
    private sealed class Handle : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Acquire_AfterCloseAllAsync_DoesNotThrowDraining()
    {
        var cache = new FileHandleCache<Handle>((path, arg) => new Handle());

        // Acquire once, then release so CloseAllAsync's RefCount==0 wait completes immediately.
        var first = cache.Acquire("test-file.bin", FileAccess.Read);
        first.Should().NotBeNull();
        cache.Release("test-file.bin");

        await cache.CloseAllAsync(CancellationToken.None);

        // Before the fix: throws InvalidOperationException("FileHandleCache is draining.")
        // After the fix: succeeds and lazily reopens a fresh handle.
        var act = () => cache.Acquire("test-file.bin", FileAccess.Read);

        act.Should().NotThrow<InvalidOperationException>(
            "CloseAllAsync is only a transient close (seed transition / move-storage fence); " +
            "the cache must remain usable afterward via lazy reopen");

        var second = act();
        second.Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_AfterCloseAllAsync_DoesNotThrowDraining()
    {
        var cache = new FileHandleCache<Handle>((path, arg) => new Handle());

        var first = await cache.AcquireAsync("test-file-async.bin", FileAccess.Read, null, CancellationToken.None);
        first.Should().NotBeNull();
        cache.Release("test-file-async.bin");

        await cache.CloseAllAsync(CancellationToken.None);

        Func<Task> act = async () =>
            await cache.AcquireAsync("test-file-async.bin", FileAccess.Read, null, CancellationToken.None);

        await act.Should().NotThrowAsync<InvalidOperationException>(
            "AcquireAsync must also recover after a transient CloseAllAsync drain");
    }

    /// <summary>
    /// Regression test for a concurrency edge case introduced by the drain-recovery fix:
    /// <see cref="FileHandleCache{THandle}.CloseAllAsync"/> was not serialized against itself.
    /// If call A's `finally { _draining = false; }` ran while call B was mid-flight (between
    /// B's RefCount wait-loop and its <c>_createLock</c>-guarded dispose), a legitimate
    /// concurrent Acquire could create a fresh in-use entry that B then disposed
    /// unconditionally, causing a transient I/O failure on that handle.
    ///
    /// This is a best-effort stress/race test: it cannot deterministically prove the race is
    /// gone, but after adding the dedicated `_closeAllLock` serializing the whole
    /// CloseAllAsync body, this should pass reliably across many iterations. Before the fix
    /// this test was observed to fail intermittently (ObjectDisposedException / disposed
    /// handle still marked as acquired) under load.
    /// </summary>
    [Fact]
    public async Task ConcurrentCloseAllAsync_InterleavedWithAcquire_NeverYieldsDisposedHandle()
    {
        const int iterations = 200;

        for (int i = 0; i < iterations; i++)
        {
            var cache = new FileHandleCache<Handle>((path, arg) => new Handle());
            var path = $"race-file-{i}.bin";

            // Seed one handle and release it so the wait-loop inside CloseAllAsync can
            // complete immediately (RefCount == 0).
            var seed = cache.Acquire(path, FileAccess.Read);
            cache.Release(path);

            // Fire two CloseAllAsync calls and one Acquire concurrently. Before the fix,
            // the two CloseAllAsync calls could interleave via the shared `_draining` flag
            // and race with the Acquire, leaving a disposed-but-referenced handle behind.
            var closeTask1 = cache.CloseAllAsync(CancellationToken.None).AsTask();
            var closeTask2 = cache.CloseAllAsync(CancellationToken.None).AsTask();
            Task<Handle> acquireTask = Task.Run(() =>
            {
                // Retry a few times since a concurrent CloseAllAsync may transiently set
                // `_draining = true` and cause Acquire to throw; that's expected/benign as
                // long as it eventually succeeds and yields a live, usable handle.
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    try
                    {
                        return cache.Acquire(path, FileAccess.Read);
                    }
                    catch (InvalidOperationException)
                    {
                        Thread.Sleep(1);
                    }
                }

                return cache.Acquire(path, FileAccess.Read);
            });

            await Task.WhenAll(closeTask1, closeTask2, acquireTask);

            var acquired = acquireTask.Result;
            acquired.Should().NotBeNull();
            acquired.Disposed.Should().BeFalse(
                $"iteration {i}: a concurrent CloseAllAsync must not dispose a handle " +
                "that a legitimate Acquire is actively holding");

            cache.Release(path);
            await cache.DisposeAsync();
        }
    }
}
