using System;

namespace vTorrent.Bencode.Torrents;

/// <summary>
/// Immutable 20-byte SHA-1 hash with value semantics.
/// </summary>
public readonly struct SHA1Hash : IEquatable<SHA1Hash>
{
    public const int Size = 20;

    private readonly byte[] _bytes;

    public SHA1Hash(byte[] bytes)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length != Size)
            throw new ArgumentException($"SHA-1 hash must be exactly {Size} bytes, got {bytes.Length}");
        _bytes = new byte[Size];
        bytes.AsSpan().CopyTo(_bytes);
    }

    public SHA1Hash(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"SHA-1 hash must be exactly {Size} bytes, got {bytes.Length}");
        _bytes = new byte[Size];
        bytes.CopyTo(_bytes);
    }

    public byte[] Bytes => _bytes ?? Array.Empty<byte>();
    public ReadOnlySpan<byte> AsSpan() => _bytes ?? ReadOnlySpan<byte>.Empty;

    public bool IsZero
    {
        get
        {
            if (_bytes is null) return true;
            foreach (var b in _bytes)
                if (b != 0) return false;
            return true;
        }
    }

    public string ToHex() => Convert.ToHexString(Bytes);

    public static SHA1Hash FromHex(string hex)
    {
        if (hex is null) throw new ArgumentNullException(nameof(hex));
        return new SHA1Hash(Convert.FromHexString(hex));
    }

    public bool Equals(SHA1Hash other) => AsSpan().SequenceEqual(other.AsSpan());
    public override bool Equals(object? obj) => obj is SHA1Hash other && Equals(other);

    public override int GetHashCode()
    {
        if (_bytes is null) return 0;
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(_bytes);
    }

    public static bool operator ==(SHA1Hash left, SHA1Hash right) => left.Equals(right);
    public static bool operator !=(SHA1Hash left, SHA1Hash right) => !left.Equals(right);

    public override string ToString() => ToHex();
}
