using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Storage;
using vTorrent.Core.PieceIO;

namespace vTorrent.Core.PieceIO.Backends;

/// <summary>
/// Creates an <see cref="IDiskBackend"/> instance appropriate for the
/// effective backend type derived from session settings and optional
/// per-torrent overrides.
/// </summary>
internal static class DiskBackendFactory
{
    /// <summary>
    /// Creates the appropriate <see cref="IDiskBackend"/> for a torrent.
    /// </summary>
    /// <param name="diskSettings">Session-wide disk settings.</param>
    /// <param name="perTorrentOverride">Optional torrent-level backend-type override.</param>
    /// <param name="perTorrentWriteMode">Optional torrent-level write-mode override.</param>
    /// <param name="sparseFileManager">Sparse-file allocator for this torrent.</param>
    /// <param name="lockManager">Per-file lock manager for this torrent.</param>
    /// <param name="logger">Logger instance.</param>
    /// <returns>A new <see cref="IDiskBackend"/> that must be disposed with the torrent.</returns>
    public static IDiskBackend Create(
        DiskSettings diskSettings,
        DiskBackendType? perTorrentOverride,
        DiskIoMode? perTorrentWriteMode,
        SparseFileManager sparseFileManager,
        IFileLockManager lockManager,
        ILogger logger,
        IOptionsMonitor<DiskSettings>? diskMonitor = null,
        DiskAccessHint accessHint = DiskAccessHint.Normal)
    {
        var effectiveType      = perTorrentOverride    ?? diskSettings.BackendType;
        var effectiveWriteMode = perTorrentWriteMode   ?? diskSettings.WriteMode;

        return effectiveType switch
        {
            DiskBackendType.ForcePosix => new PosixDiskBackend(
                sparseFileManager, lockManager, diskSettings, effectiveWriteMode, logger, diskMonitor, accessHint),

            DiskBackendType.ForceMmap  => new MmapDiskBackend(
                sparseFileManager, lockManager, diskSettings, logger, diskMonitor, accessHint),

            _ /* Auto */               => new AdaptiveDiskBackend(
                sparseFileManager, lockManager, diskSettings, effectiveWriteMode, logger, diskMonitor, accessHint)
        };
    }
}
