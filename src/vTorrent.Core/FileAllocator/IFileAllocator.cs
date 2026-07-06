using vTorrent.Bencode.Torrents;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace vTorrent.Core.FileAllocator
{
    public interface IFileAllocator
    {

        AllocationResult AllocateFile(string filePath, long size, AllocationStrategy strategy);
        Task<AllocationResult> AllocateFileAsync(string filePath, long size, AllocationStrategy strategy, CancellationToken cancellationToken = default);
        AllocationResult AllocateFiles(string basePath, TorrentInfo torrentInfo, AllocationStrategy strategy);
        Task<AllocationResult> AllocateFilesAsync(string basePath, TorrentInfo torrentInfo, AllocationStrategy strategy, IProgress<AllocationProgress> progress = null, CancellationToken cancellationToken = default);
        AllocationStatus CheckAllocation(string basePath, TorrentInfo torrentInfo);
    }
}
