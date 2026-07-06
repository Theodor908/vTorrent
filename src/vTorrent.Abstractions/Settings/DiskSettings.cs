namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Disk I/O settings
/// </summary>
public class DiskSettings
{
    /// <summary>
    /// Disk cache size in bytes
    /// </summary>
    public long CacheSize { get; set; } = 64 * 1024 * 1024; // 64 MB

    /// <summary>
    /// Default save path for downloads
    /// </summary>
    public string DefaultSavePath { get; set; } = "";

    /// <summary>
    /// Path for incomplete downloads (empty = same as save path)
    /// </summary>
    public string IncompleteSavePath { get; set; } = "";

    /// <summary>
    /// Pre-allocate files on disk
    /// </summary>
    public bool PreallocateFiles { get; set; } = false;

    /// <summary>
    /// Number of threads for hash verification
    /// </summary>
    public int HashThreads { get; set; } = 2;

    /// <summary>
    /// Maximum outstanding disk requests
    /// </summary>
    public int MaxOutstandingDiskRequests { get; set; } = 64;

    /// <summary>Skip file verification when loading incomplete resume data. Trusts resume data piece states. libtorrent default: false.</summary>
    public bool NoRecheckIncompleteResume { get; set; } = false;

    /// <summary>Enable extent grouping in piece picker for disk I/O locality. libtorrent default: false</summary>
    public bool PieceExtentAffinity { get; set; } = false;

    /// <summary>Extent size in bytes for piece grouping. Default: 4 MiB (libtorrent default)</summary>
    public int PieceExtentSize { get; set; } = 4_194_304;

    // === Backend selection ===

    /// <summary>Disk I/O backend selection strategy. Auto = adaptive per-file routing based on file size. libtorrent default: auto</summary>
    public DiskBackendType BackendType { get; set; } = DiskBackendType.Auto;

    /// <summary>OS cache behavior for read operations. libtorrent default: enableOsCache</summary>
    public DiskIoMode ReadMode { get; set; } = DiskIoMode.EnableOsCache;

    /// <summary>OS cache behavior for write operations. libtorrent default: enableOsCache</summary>
    public DiskIoMode WriteMode { get; set; } = DiskIoMode.EnableOsCache;

    /// <summary>File size cutoff in 16 KiB blocks below which the Posix backend is preferred over mmap. Default: 40 blocks (640 KiB)</summary>
    public int MmapFileSizeCutoff { get; set; } = 40;  // 640 KiB in 16 KiB blocks

    /// <summary>Maximum total bytes of memory-mapped address space. Default: 4 GiB. libtorrent default: 4 GB</summary>
    public long MmapMemoryCeiling { get; set; } = 4L * 1024 * 1024 * 1024;  // 4 GB

    // === File handle management ===

    /// <summary>
    /// Interval in seconds between automatic file handle close sweeps. -1 = sentinel — SettingsSeeder sets platform value.
    /// Windows default: 240 s. POSIX default: 0 (disabled).
    /// </summary>
    public int CloseFileInterval { get; set; } = -1;  // Sentinel — SettingsSeeder sets platform value

    // === Write backpressure ===

    /// <summary>Maximum bytes queued for disk writes before download is throttled. 0 = auto-tune (vTorrent default). libtorrent default: 1 MiB fixed.</summary>
    public long MaxQueuedDiskBytes { get; set; } = 0;  // 0 = auto-tune; libtorrent uses 1048576 (1 MiB) fixed

    // === Disk space monitoring ===

    /// <summary>Free-space threshold in bytes below which a warning event is raised. Default: 1 GiB</summary>
    public long DiskSpaceWarningBytes { get; set; } = 1L * 1024 * 1024 * 1024;  // 1 GB

    /// <summary>Free-space threshold in bytes below which downloads are paused. Default: 100 MiB</summary>
    public long DiskSpaceCriticalBytes { get; set; } = 100L * 1024 * 1024;  // 100 MB

    // === Error recovery ===

    /// <summary>Seconds to wait before retrying a failed disk operation optimistically. libtorrent default: 600</summary>
    public int OptimisticDiskRetry { get; set; } = 600;

    /// <summary>Maximum number of consecutive disk error retries before a torrent is paused. 0 = infinite. vTorrent default: 5. libtorrent retries infinitely.</summary>
    public int MaxDiskRetries { get; set; } = 5;

    // === Verification pipeline ===

    /// <summary>Memory budget in 16 KiB blocks for the piece verification pipeline. Default: 256 blocks (4 MiB). libtorrent default: 256</summary>
    public int CheckingMemUsage { get; set; } = 256;  // 4 MiB in 16 KiB blocks

    /// <summary>
    /// Use O_NOATIME flag when opening files on Linux to reduce disk wear.
    /// No-op on Windows/macOS. libtorrent: no_atime_storage. Default: true.
    /// </summary>
    public bool NoAtimeStorage { get; set; } = true;

    /// <summary>
    /// Debug-only: skip piece hash verification. WARNING: allows corrupt data.
    /// libtorrent: disable_hash_checks. Default: false.
    /// </summary>
    public bool DisableHashChecks { get; set; } = false;

    /// <summary>Maximum open file handles in the file pool. libtorrent: file_pool_size. Default: 40.</summary>
    public int FilePoolSize { get; set; } = 40;
}
