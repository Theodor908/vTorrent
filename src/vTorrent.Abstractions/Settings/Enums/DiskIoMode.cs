namespace vTorrent.Abstractions.Settings;

/// <summary>OS cache behavior for disk I/O operations.</summary>
public enum DiskIoMode
{
    EnableOsCache,      // Normal buffered I/O (default)
    WriteThrough,       // OS caches reads, writes flush immediately
    DisableOsCache      // Bypass OS page cache (posix-only, falls back to WriteThrough on mmap)
}
