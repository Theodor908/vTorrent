using System;
using System.Security.Cryptography;
using System.Text;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// Immutable I2P destination identifier. Holds the 32-byte SHA-256 hash
/// of the full destination, and optionally the full base64 destination string.
/// Implemented as sealed class (not struct) because it holds a byte[] and optional string,
/// matching the pattern of System.Net.IPAddress. Value-type equality semantics via IEquatable.
/// </summary>
public sealed class I2pDestination : IEquatable<I2pDestination>
{
    public const int HashLength = 32;

    private readonly byte[] _hash;

    private I2pDestination(byte[] hash, string? base64Destination = null)
    {
        _hash = hash;
        Base64Destination = base64Destination;
    }

    /// <summary>The 32-byte SHA-256 destination hash.</summary>
    public ReadOnlySpan<byte> Hash => _hash;

    /// <summary>Optional full base64 destination (387+ bytes decoded).</summary>
    public string? Base64Destination { get; }

    public static I2pDestination FromHash(byte[] hash)
    {
        if (hash == null || hash.Length != HashLength)
            throw new ArgumentException($"I2P destination hash must be exactly {HashLength} bytes", nameof(hash));
        var copy = new byte[HashLength];
        Buffer.BlockCopy(hash, 0, copy, 0, HashLength);
        return new I2pDestination(copy);
    }

    public static I2pDestination FromHash(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != HashLength)
            throw new ArgumentException($"I2P destination hash must be exactly {HashLength} bytes", nameof(hash));
        return new I2pDestination(hash.ToArray());
    }

    public static I2pDestination FromCompact(ReadOnlySpan<byte> compact) => FromHash(compact);

    public static I2pDestination FromBase64(string base64Destination)
    {
        if (string.IsNullOrEmpty(base64Destination))
            throw new ArgumentException("Base64 destination cannot be empty", nameof(base64Destination));

        // I2P uses a modified Base64 alphabet: '+' → '-', '/' → '~'
        // Convert from I2P Base64 to standard Base64 for .NET's parser
        var standardBase64 = base64Destination.Replace('-', '+').Replace('~', '/');

        var decoded = Convert.FromBase64String(standardBase64);
        var hash = SHA256.HashData(decoded);
        return new I2pDestination(hash, base64Destination);
    }

    public I2pDestination WithBase64(string base64Destination) =>
        new I2pDestination((byte[])_hash.Clone(), base64Destination);

    public byte[] ToCompact() => (byte[])_hash.Clone();

    public string ToBase32()
    {
        var b32 = Base32Encode(_hash).ToLowerInvariant();
        return $"{b32}.b32.i2p";
    }

    public string ToHex() => Convert.ToHexString(_hash).ToLowerInvariant();

    public bool Equals(I2pDestination? other)
    {
        if (other is null) return false;
        return _hash.AsSpan().SequenceEqual(other._hash);
    }

    public override bool Equals(object? obj) => Equals(obj as I2pDestination);

    public override int GetHashCode() => HashCode.Combine(
        BitConverter.ToInt32(_hash, 0), BitConverter.ToInt32(_hash, 4));

    public override string ToString()
    {
        var hex = ToHex();
        return hex.Length > 12 ? $"{hex[..12]}..." : hex;
    }

    public static bool operator ==(I2pDestination? left, I2pDestination? right) =>
        left is null ? right is null : left.Equals(right);
    public static bool operator !=(I2pDestination? left, I2pDestination? right) => !(left == right);

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }
        if (bitsLeft > 0)
            sb.Append(alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        return sb.ToString();
    }
}
