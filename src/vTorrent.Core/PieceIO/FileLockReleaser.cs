using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace vTorrent.Core.PieceIO
{
    public class FileLockReleaser : IDisposable
    {
        private readonly FileLockManager _lockManager;
        private readonly string _normalizedPath;
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public FileLockReleaser(FileLockManager lockManager, string normalizedPath, SemaphoreSlim semaphore)
        {
            _lockManager = lockManager;
            _normalizedPath = normalizedPath;
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if(_disposed) return;

            _disposed = true;

            _lockManager.ReleaseLock(_normalizedPath, _semaphore);
        }
    }
}
