using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;

namespace vTorrent.Core.DHT;

/// <summary>
/// BEP 33 bloom filter for DHT scrape.
/// Fixed parameters: k=2 hash functions, m=2048 bits (256 bytes).
/// Uses SHA-1 of IP address to derive two 16-bit indices.
/// Index extraction is little-endian and bit ordering is LSB-first per BEP 33 specification.
/// </summary>
public class BloomFilter
{
    public const int FilterSizeBytes = 256;
    public const int FilterSizeBits = 2048;
    public const int HashFunctionCount = 2;

    private readonly byte[] _bits;

    public BloomFilter()
    {
        _bits = new byte[FilterSizeBytes];
    }

    public BloomFilter(ReadOnlySpan<byte> data)
    {
        if (data.Length != FilterSizeBytes)
            throw new ArgumentException($"Bloom filter must be exactly {FilterSizeBytes} bytes", nameof(data));
        _bits = data.ToArray();
    }

    public ReadOnlySpan<byte> Data => _bits;

    public void Add(IPAddress ip)
    {
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(ip.GetAddressBytes(), hash);

        int index1 = BinaryPrimitives.ReadUInt16LittleEndian(hash[..2]) % FilterSizeBits;
        int index2 = BinaryPrimitives.ReadUInt16LittleEndian(hash[2..4]) % FilterSizeBits;

        // LSB-first bit indexing per BEP 33 specification
        _bits[index1 / 8] |= (byte)(0x01 << (index1 % 8));
        _bits[index2 / 8] |= (byte)(0x01 << (index2 % 8));
    }

    public void Union(ReadOnlySpan<byte> other)
    {
        if (other.Length != FilterSizeBytes) return;
        for (int i = 0; i < FilterSizeBytes; i++)
            _bits[i] |= other[i];
    }

    public double EstimateCount()
    {
        int zeroBits = CountZeroBits();
        if (zeroBits == FilterSizeBits) return 0; // empty filter

        int c = Math.Min(FilterSizeBits - 1, zeroBits);
        if (c == 0) return double.PositiveInfinity; // saturated

        return Math.Log((double)c / FilterSizeBits) /
               (HashFunctionCount * Math.Log(1.0 - 1.0 / FilterSizeBits));
    }

    public bool IsSaturated() => CountZeroBits() == 0;

    private int CountZeroBits()
    {
        int count = 0;
        foreach (byte b in _bits)
            count += BitOperations.PopCount((uint)(byte)~b);
        return count;
    }
}
