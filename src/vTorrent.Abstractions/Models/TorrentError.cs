namespace vTorrent.Abstractions.Models;

/// <summary>
/// Rich error information for torrent failures.
/// Replaces the previous Health=Error + ErrorMessage pattern.
/// </summary>
public readonly record struct TorrentError
{
    /// <summary>Human-readable error description.</summary>
    public required string Message { get; init; }

    /// <summary>Machine-readable error code (e.g., "DiskFull", "HashMismatch").</summary>
    public string? ErrorCode { get; init; }

    /// <summary>File path that caused the error, if applicable.</summary>
    public string? FilePath { get; init; }
}
