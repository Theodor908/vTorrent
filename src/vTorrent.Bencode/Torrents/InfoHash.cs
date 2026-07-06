using System;

namespace vTorrent.Bencode.Torrents;

/// <summary>
/// Composite info hash carrying v1 (SHA-1) and/or v2 (SHA-256) hashes.
/// Provides PrimaryHex for backward-compatible string keying.
/// </summary>
public readonly struct InfoHash : IEquatable<InfoHash>
{
    public SHA1Hash? V1 { get; init; }
    public SHA256Hash? V2 { get; init; }

    public bool HasV1 => V1.HasValue && !V1.Value.IsZero;
    public bool HasV2 => V2.HasValue && !V2.Value.IsZero;
    public bool IsHybrid => HasV1 && HasV2;

    public TorrentVersion Version => (HasV1, HasV2) switch
    {
        (true, true) => TorrentVersion.Hybrid,
        (true, false) => TorrentVersion.V1,
        (false, true) => TorrentVersion.V2,
        _ => throw new InvalidOperationException("InfoHash has neither v1 nor v2 hash")
    };

    /// <summary>
    /// Primary 40-char hex identifier for DB keys and existing code.
    /// Returns v1 hex if available, otherwise truncated v2 (first 20 bytes).
    /// </summary>
    public string PrimaryHex
    {
        get
        {
            if (HasV1) return V1!.Value.ToHex();
            if (HasV2) return Convert.ToHexString(V2!.Value.AsSpan()[..SHA1Hash.Size]);
            throw new InvalidOperationException("InfoHash has neither v1 nor v2 hash");
        }
    }

    public bool Equals(InfoHash other) =>
        Nullable.Equals(V1, other.V1) && Nullable.Equals(V2, other.V2);

    public override bool Equals(object? obj) => obj is InfoHash other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(V1, V2);

    public static bool operator ==(InfoHash left, InfoHash right) => left.Equals(right);
    public static bool operator !=(InfoHash left, InfoHash right) => !left.Equals(right);

    public override string ToString() => IsHybrid
        ? $"Hybrid(v1={V1!.Value.ToHex()[..8]}…, v2={V2!.Value.ToHex()[..8]}…)"
        : HasV1
            ? $"V1({V1!.Value.ToHex()[..8]}…)"
            : $"V2({V2!.Value.ToHex()[..8]}…)";
}
