using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace vTorrent.Core.PieceIO
{
    public class FileLockManager : IFileLockManager
    {

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks;
        private bool _disposed;

        public FileLockManager()
        {
            _fileLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
        }
        
        public IDisposable AcquireLock(string filePath)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AcquireLock));
            }

            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentNullException("File path cannot be null or empty", nameof(filePath));
            }

            var normalizedPath = Path.GetFullPath(filePath);

            var semaphore = _fileLocks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));

            // Use timeout to prevent indefinite blocking - 30 seconds is generous for file I/O
            if (!semaphore.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException($"Timed out waiting for file lock on: {normalizedPath}");
            }

            return new FileLockReleaser(this, normalizedPath, semaphore);
        }

        public async Task<IDisposable> AcquireLockAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AcquireLock));
            }

            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentNullException("File path cannot be null or empty", nameof (filePath));
            }

            var normalizedPath = Path.GetFullPath(filePath);

            var semaphore = _fileLocks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));

            // Use timeout to prevent indefinite blocking - 30 seconds is generous for file I/O
            if (!await semaphore.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken))
            {
                throw new TimeoutException($"Timed out waiting for file lock on: {normalizedPath}");
            }

            return new FileLockReleaser(this, normalizedPath, semaphore);
        }

        public void ReleaseLock(string normalizedPath, SemaphoreSlim semaphore)
        {
            semaphore.Release();
            // Semaphores kept alive for manager lifetime.
            // File count is bounded by torrent file count, so memory is bounded.
        }

        public void Dispose()
        {
            if(_disposed) return;

            _disposed = true;

            foreach (var semaphore in _fileLocks.Values)
            {
                semaphore?.Dispose();
            }

            _fileLocks.Clear();
        }
    }
}
