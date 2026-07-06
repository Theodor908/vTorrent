namespace vTorrent.Core.PieceIO;

/// <summary>
/// Hint for disk backend I/O optimization.
/// <see cref="CheckingMode"/> enables sequential-scan and volatile-read OS hints
/// matching libtorrent's <c>sequential_access | volatile_read</c> flags during piece checking.
/// </summary>
public enum DiskAccessHint
{
    /// <summary>Default random-access I/O (download/upload path).</summary>
    Normal,

    /// <summary>
    /// Sequential-access with volatile reads (checking path).
    /// Enables OS prefetch and prevents page cache pollution.
    /// </summary>
    CheckingMode,
}
