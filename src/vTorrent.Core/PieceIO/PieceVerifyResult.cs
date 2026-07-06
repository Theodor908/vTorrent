namespace vTorrent.Core.PieceIO;

/// <summary>
/// Result of piece hash verification. Distinguishes between V1/V2 failures
/// and detects inconsistency in hybrid torrents (V1 pass + V2 fail or vice versa).
/// </summary>
public enum PieceVerifyResult
{
    /// <summary>All applicable hashes passed.</summary>
    Valid,

    /// <summary>V1 SHA-1 hash failed (or no data provided).</summary>
    CorruptV1,

    /// <summary>V2 merkle block hash failed.</summary>
    CorruptV2,

    /// <summary>One hash type passed but the other failed (hybrid torrent only).</summary>
    Inconsistent,
}
