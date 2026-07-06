using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace vTorrent.Core.PieceIO
{
    public interface IFileLockManager : IDisposable
    {
        Task<IDisposable> AcquireLockAsync(string filePath, CancellationToken cancellationToken = default);
        IDisposable AcquireLock(string filePath);
        void ReleaseLock(string filePath, SemaphoreSlim semaphore);
    }
}
