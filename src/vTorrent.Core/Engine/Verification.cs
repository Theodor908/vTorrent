using System;
using System.Collections.Generic;

namespace vTorrent.Core.Engine;

/// <summary>
/// Verification mode for file integrity checking
/// </summary>
public enum VerificationMode
{
    /// <summary>
    /// Verify all pieces (thorough but slow)
    /// </summary>
    Full,

    /// <summary>
    /// Only verify pieces marked as complete in bitfield (fast)
    /// </summary>
    QuickCheck,

    /// <summary>
    /// Verify specific piece range
    /// </summary>
    Selective
}

/// <summary>
/// Options for file integrity verification
/// </summary>
public class VerificationOptions
{
    /// <summary>
    /// Verification mode to use
    /// </summary>
    public VerificationMode Mode { get; set; } = VerificationMode.Full;

    /// <summary>
    /// Specific piece indices to verify (for Selective mode)
    /// </summary>
    public IEnumerable<int> PieceRange { get; set; }

    /// <summary>
    /// Maximum degree of parallelism for verification
    /// </summary>
    public int? MaxDegreeOfParallelism { get; set; }

    /// <summary>
    /// Automatically mark corrupt pieces as incomplete for re-download
    /// </summary>
    public bool AutoRedownloadCorrupt { get; set; } = true;

    /// <summary>
    /// Default verification options (full verification)
    /// </summary>
    public static VerificationOptions Default => new VerificationOptions();

    /// <summary>
    /// Quick check options (only verify completed pieces)
    /// </summary>
    public static VerificationOptions QuickCheck => new VerificationOptions
    {
        Mode = VerificationMode.QuickCheck
    };
}

/// <summary>
/// Progress report during verification
/// </summary>
public class VerificationProgress
{
    /// <summary>
    /// Total number of pieces to verify
    /// </summary>
    public int TotalPieces { get; set; }

    /// <summary>
    /// Number of pieces verified so far
    /// </summary>
    public int VerifiedPieces { get; set; }

    /// <summary>
    /// Number of corrupt pieces found
    /// </summary>
    public int CorruptCount { get; set; }

    /// <summary>
    /// Verification progress as a percentage (0.0 to 1.0)
    /// </summary>
    public double Percentage { get; set; }

    public override string ToString() =>
        $"Verifying: {VerifiedPieces}/{TotalPieces} ({Percentage:P2}, {CorruptCount} corrupt)";
}

/// <summary>
/// Result of file integrity verification
/// </summary>
public class VerificationResult
{
    /// <summary>
    /// Total number of pieces in the torrent
    /// </summary>
    public int TotalPieces { get; set; }

    /// <summary>
    /// List of piece indices that passed verification
    /// </summary>
    public List<int> VerifiedPieces { get; set; } = new();

    /// <summary>
    /// List of piece indices that failed verification (corrupt)
    /// </summary>
    public List<int> CorruptPieces { get; set; } = new();

    /// <summary>
    /// List of piece indices that could not be read (missing/unreadable)
    /// </summary>
    public List<int> MissingPieces { get; set; } = new();

    /// <summary>
    /// List of piece indices where V1 and V2 hashes are inconsistent (hybrid torrents)
    /// </summary>
    public List<int> InconsistentPieces { get; set; } = new();

    /// <summary>
    /// When verification started
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// When verification ended
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Duration of verification
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// True if all pieces verified successfully (no corrupt or missing pieces)
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// True if verification was cancelled by user
    /// </summary>
    public bool Cancelled { get; set; }

    /// <summary>
    /// Exception that occurred during verification, if any
    /// </summary>
    public Exception Error { get; set; }

    /// <summary>
    /// Progress percentage (verified pieces / total pieces)
    /// </summary>
    public double ProgressPercentage => TotalPieces > 0 ? (double)VerifiedPieces.Count / TotalPieces : 0;

    /// <summary>
    /// Human-readable summary of verification results
    /// </summary>
    public string Summary =>
        $"{VerifiedPieces.Count}/{TotalPieces} verified, " +
        $"{CorruptPieces.Count} corrupt, " +
        $"{MissingPieces.Count} missing";
}

/// <summary>
/// Event arguments for integrity verification completion
/// </summary>
public class IntegrityVerificationEventArgs : EventArgs
{
    public VerificationResult Result { get; }

    public IntegrityVerificationEventArgs(VerificationResult result)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }
}
