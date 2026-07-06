namespace vTorrent.Abstractions.Settings;

/// <summary>Disk I/O backend selection strategy.</summary>
public enum DiskBackendType
{
    Auto,           // Adaptive per-file routing (default)
    ForcePosix,     // Always use RandomAccess/pread/pwrite
    ForceMmap       // Always use MemoryMappedFile
}
