using System;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Core.PieceIO
{
    /// <summary>
    /// Implements libtorrent-style disk fence for coordinating move_storage operations.
    /// When raised, blocks new disk I/O jobs and waits for pending operations to complete.
    /// This allows safe file movement while maintaining peer connections.
    /// </summary>
    public sealed class DiskFence : IDisposable
    {
        private readonly SemaphoreSlim _fenceGate = new(1, 1);
        private readonly object _stateLock = new();
        private TaskCompletionSource<bool> _fenceWaitTcs;
        private volatile bool _isFenced;
        private int _pendingOperations;
        private bool _disposed;

        /// <summary>
        /// Whether the fence is currently raised (blocking new I/O).
        /// </summary>
        public bool IsFenced => _isFenced;

        /// <summary>
        /// Number of I/O operations currently in progress.
        /// </summary>
        public int PendingOperations => Volatile.Read(ref _pendingOperations);

        /// <summary>
        /// Raises the fence, blocking new I/O and waiting for pending operations to complete.
        /// </summary>
        /// <param name="timeout">Maximum time to wait for pending operations to drain.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if fence was raised and all operations drained, false if timeout.</returns>
        public async Task<bool> RaiseFenceAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DiskFence));

            await _fenceGate.WaitAsync(ct);
            try
            {
                if (_isFenced)
                    return true; // Already fenced

                lock (_stateLock)
                {
                    _isFenced = true;

                    // If no pending operations, we're done
                    if (_pendingOperations == 0)
                        return true;

                    // Create TCS to wait for pending operations to drain
                    _fenceWaitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                // Wait for all pending operations to complete
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);

                try
                {
                    await _fenceWaitTcs.Task.WaitAsync(timeoutCts.Token);
                    return true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout - fence is raised but operations didn't drain in time
                    return false;
                }
            }
            finally
            {
                _fenceGate.Release();
            }
        }

        /// <summary>
        /// Lowers the fence, allowing I/O to resume.
        /// </summary>
        public void LowerFence()
        {
            if (_disposed)
                return;

            lock (_stateLock)
            {
                _isFenced = false;
                _fenceWaitTcs = null;
            }
        }

        /// <summary>
        /// Acquires permission to perform an I/O operation.
        /// Throws if fence is raised, allowing caller to skip the operation.
        /// Returns an IDisposable token that must be disposed when operation completes.
        /// </summary>
        /// <exception cref="FencedException">Thrown when fence is raised.</exception>
        public IoPermit AcquirePermit()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DiskFence));

            lock (_stateLock)
            {
                if (_isFenced)
                    throw new FencedException("Disk I/O is fenced for move_storage operation");

                Interlocked.Increment(ref _pendingOperations);
                return new IoPermit(this);
            }
        }

        /// <summary>
        /// Tries to acquire permission to perform an I/O operation.
        /// Returns null if fence is raised.
        /// </summary>
        public IoPermit? TryAcquirePermit()
        {
            if (_disposed)
                return null;

            lock (_stateLock)
            {
                if (_isFenced)
                    return null;

                Interlocked.Increment(ref _pendingOperations);
                return new IoPermit(this);
            }
        }

        /// <summary>
        /// Called when an I/O operation completes.
        /// </summary>
        internal void ReleasePermit()
        {
            TaskCompletionSource<bool> tcs = null;

            lock (_stateLock)
            {
                var remaining = Interlocked.Decrement(ref _pendingOperations);

                // If fenced and no more pending operations, signal completion
                if (_isFenced && remaining == 0 && _fenceWaitTcs != null)
                {
                    tcs = _fenceWaitTcs;
                }
            }

            // Complete outside lock to avoid deadlocks
            tcs?.TrySetResult(true);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _fenceGate.Dispose();

            lock (_stateLock)
            {
                _fenceWaitTcs?.TrySetCanceled();
            }
        }
    }

    /// <summary>
    /// RAII-style permit for disk I/O operations.
    /// Dispose when operation is complete.
    /// </summary>
    public readonly struct IoPermit : IDisposable
    {
        private readonly DiskFence _fence;

        internal IoPermit(DiskFence fence)
        {
            _fence = fence;
        }

        public void Dispose()
        {
            _fence?.ReleasePermit();
        }
    }

    /// <summary>
    /// Exception thrown when attempting I/O while fence is raised.
    /// </summary>
    public class FencedException : InvalidOperationException
    {
        public FencedException(string message) : base(message) { }
    }
}
