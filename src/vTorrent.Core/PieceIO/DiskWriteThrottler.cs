using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Core.Utilities;

namespace vTorrent.Core.PieceIO
{
    /// <summary>
    /// Implements libtorrent-style disk_buffer_pool 3-tier watermark backpressure.
    /// Throttles peer downloads when the disk write queue fills up to prevent
    /// unbounded memory growth while maintaining throughput.
    /// </summary>
    internal sealed class DiskWriteThrottler : IDisposable
    {
        private long _pendingBytes;               // Interlocked — current queued bytes
        private long _effectiveLimit;             // Auto-tuned or manual ceiling
        private readonly long _maxLimit;          // DiskSettings.MaxQueuedDiskBytes (0 = auto)
        private readonly SemaphoreSlim _gate = new(1, 1); // Binary gate for HardPause
        private volatile bool _isPaused;          // Track if gate is closed
        private readonly ThroughputMeter _meter;  // For auto-tuning
        private readonly ILogger _logger;
        private bool _disposed;

        /// <summary>Current bytes queued awaiting disk flush.</summary>
        public long PendingBytes => Interlocked.Read(ref _pendingBytes);

        /// <summary>Current effective write-queue ceiling in bytes.</summary>
        public long EffectiveLimit => Interlocked.Read(ref _effectiveLimit);

        /// <summary>Whether writes are currently hard-paused at the gate.</summary>
        public bool IsPaused => _isPaused;

        /// <param name="maxQueuedDiskBytes">
        /// Maximum bytes queued for disk writes (DiskSettings.MaxQueuedDiskBytes).
        /// 0 = auto-tune based on measured disk throughput.
        /// </param>
        /// <param name="logger">Logger instance.</param>
        internal DiskWriteThrottler(long maxQueuedDiskBytes, ILogger logger)
        {
            _maxLimit = maxQueuedDiskBytes;
            _effectiveLimit = maxQueuedDiskBytes > 0 ? maxQueuedDiskBytes : 1024 * 1024; // Start at 1 MB
            _meter = new ThroughputMeter();
            _logger = logger;
        }

        /// <summary>
        /// Called before each disk write. Blocks when the queue exceeds 75% of the
        /// effective limit (HardPause), logs a warning between 50-75% (SoftPressure).
        /// The write size is added to the pending counter here; caller must call
        /// <see cref="OnWriteCompleted"/> with the same size after the write finishes.
        /// </summary>
        public async ValueTask WaitIfThrottledAsync(int writeSize, CancellationToken ct)
        {
            var pending = Interlocked.Add(ref _pendingBytes, writeSize);
            var limit   = Interlocked.Read(ref _effectiveLimit);

            if (pending > limit * 3 / 4)  // > 75% → HardPause
            {
                if (!_isPaused)
                {
                    _isPaused = true;
                    _logger.LogWarning(
                        "Disk write backpressure: HardPause at {Pending}/{Limit} bytes",
                        pending, limit);
                }
                await _gate.WaitAsync(ct);
                _gate.Release(); // Let next waiter also check — gate re-closes in OnWriteCompleted if still above watermark
            }
            else if (pending > limit / 2)  // > 50% → SoftPressure
            {
                _logger.LogDebug(
                    "Disk write queue soft pressure: {Pending}/{Limit} bytes",
                    pending, limit);
            }
        }

        /// <summary>
        /// Called after each disk write completes. Decrements the pending counter,
        /// records throughput, and releases the gate when the queue drains to the
        /// 50% low watermark (matches libtorrent's m_low_watermark = m_max_use / 2).
        /// </summary>
        public void OnWriteCompleted(int bytesWritten)
        {
            var pending = Interlocked.Add(ref _pendingBytes, -bytesWritten);
            _meter.Record(bytesWritten);

            var limit = Interlocked.Read(ref _effectiveLimit);

            // Resume at low watermark (50%) — matches libtorrent
            if (_isPaused && pending <= limit / 2)
            {
                _isPaused = false;
                try { _gate.Release(); }
                catch (SemaphoreFullException) { /* gate was already open */ }
                _logger.LogDebug(
                    "Disk write backpressure: resumed at {Pending}/{Limit} bytes",
                    pending, limit);
            }

            // Auto-tune if in auto mode
            if (_maxLimit == 0)
                AutoTune();
        }

        /// <summary>
        /// Adjusts the effective limit based on measured disk throughput.
        /// Targets 2.5 seconds of write buffer, clamped to [1 MiB, 256 MiB].
        /// </summary>
        private void AutoTune()
        {
            var bytesPerSecond = _meter.BytesPerSecond;
            if (bytesPerSecond <= 0) return;

            // Target: 2.5 seconds of write buffer
            var target = (long)(bytesPerSecond * 2.5);

            // Clamp: floor 1 MB, ceiling 256 MB (prevent runaway)
            target = Math.Clamp(target, 1024 * 1024, 256L * 1024 * 1024);

            Interlocked.Exchange(ref _effectiveLimit, target);
        }

        /// <summary>
        /// Called by DiskSpaceMonitor when free space drops below the critical threshold.
        /// Sets the effective limit to 0 to pause all new writes immediately.
        /// </summary>
        public void OnDiskSpaceCritical()
        {
            Interlocked.Exchange(ref _effectiveLimit, 0); // Pause all writes
            _isPaused = true;
        }

        /// <summary>
        /// Called by DiskSpaceMonitor when free space recovers above the critical threshold.
        /// Restores the effective limit and releases the gate.
        /// </summary>
        public void OnDiskSpaceOk()
        {
            if (_maxLimit > 0)
                Interlocked.Exchange(ref _effectiveLimit, _maxLimit);
            else
                Interlocked.Exchange(ref _effectiveLimit, 1024 * 1024); // Reset auto-tune baseline
            _isPaused = false;
            try { _gate.Release(); }
            catch (SemaphoreFullException) { /* gate was already open */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _gate.Dispose();
        }

    }
}
