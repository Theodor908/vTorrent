using FluentAssertions;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class SHA256HashTests
{
    [Fact]
    public void Constructor_ValidBytes_StoresCorrectly()
    {
        var bytes = new byte[32];
        bytes[0] = 0xAB;
        bytes[31] = 0xCD;

        var hash = new SHA256Hash(bytes);

        hash.Bytes.Length.Should().Be(32);
        hash.Bytes[0].Should().Be(0xAB);
        hash.Bytes[31].Should().Be(0xCD);
    }

    [Fact]
    public void Constructor_WrongLength_Throws()
    {
        var act = () => new SHA256Hash(new byte[20]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToHex_ReturnsUppercase64Chars()
    {
        var bytes = new byte[32];
        bytes[0] = 0xFF;
        var hash = new SHA256Hash(bytes);

        hash.ToHex().Should().HaveLength(64);
        hash.ToHex().Should().StartWith("FF");
    }

    [Fact]
    public void Equality_SameBytes_AreEqual()
    {
        var bytes = new byte[32];
        bytes[5] = 42;
        var a = new SHA256Hash(bytes);
        var b = new SHA256Hash((byte[])bytes.Clone());

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentBytes_NotEqual()
    {
        var a = new SHA256Hash(new byte[32]);
        var bBytes = new byte[32];
        bBytes[0] = 1;
        var b = new SHA256Hash(bBytes);

        a.Should().NotBe(b);
    }

    [Fact]
    public void IsZero_AllZeros_ReturnsTrue()
    {
        var hash = new SHA256Hash(new byte[32]);
        hash.IsZero.Should().BeTrue();
    }

    [Fact]
    public void IsZero_NonZero_ReturnsFalse()
    {
        var bytes = new byte[32];
        bytes[15] = 1;
        new SHA256Hash(bytes).IsZero.Should().BeFalse();
    }

    [Fact]
    public void AsSpan_ReturnsReadOnlyView()
    {
        var bytes = new byte[32];
        bytes[0] = 99;
        var hash = new SHA256Hash(bytes);

        hash.AsSpan()[0].Should().Be(99);
        hash.AsSpan().Length.Should().Be(32);
    }

    [Fact]
    public void FromHex_ValidHex_RoundTrips()
    {
        var bytes = new byte[32];
        bytes[0] = 0xDE; bytes[1] = 0xAD;
        var original = new SHA256Hash(bytes);

        var restored = SHA256Hash.FromHex(original.ToHex());
        restored.Should().Be(original);
    }

    [Fact]
    public void Constructor_DefensiveCopy_DoesNotMutate()
    {
        var bytes = new byte[32];
        var hash = new SHA256Hash(bytes);
        bytes[0] = 0xFF;

        hash.Bytes[0].Should().Be(0);
    }
}
