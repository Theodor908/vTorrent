using FluentAssertions;
using vTorrent.Bencode.Torrents;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class SHA1HashTests
{
    [Fact]
    public void Constructor_ValidBytes_StoresCorrectly()
    {
        var bytes = new byte[20];
        bytes[0] = 0xAB;
        var hash = new SHA1Hash(bytes);
        hash.Bytes.Length.Should().Be(20);
        hash.Bytes[0].Should().Be(0xAB);
    }

    [Fact]
    public void Constructor_WrongLength_Throws()
    {
        var act = () => new SHA1Hash(new byte[32]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToHex_Returns40Chars()
    {
        var hash = new SHA1Hash(new byte[20]);
        hash.ToHex().Should().HaveLength(40);
    }

    [Fact]
    public void Equality_SameBytes_AreEqual()
    {
        var bytes = new byte[20]; bytes[3] = 7;
        var a = new SHA1Hash(bytes);
        var b = new SHA1Hash((byte[])bytes.Clone());
        a.Should().Be(b);
    }

    [Fact]
    public void FromHex_RoundTrips()
    {
        var bytes = new byte[20]; bytes[0] = 0xCA; bytes[1] = 0xFE;
        var original = new SHA1Hash(bytes);
        SHA1Hash.FromHex(original.ToHex()).Should().Be(original);
    }

    [Fact]
    public void Constructor_DefensiveCopy()
    {
        var bytes = new byte[20];
        var hash = new SHA1Hash(bytes);
        bytes[0] = 0xFF;
        hash.Bytes[0].Should().Be(0);
    }
}
