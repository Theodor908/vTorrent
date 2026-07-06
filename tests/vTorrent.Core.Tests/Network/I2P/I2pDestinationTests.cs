using FluentAssertions;
using Xunit;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Tests.Network.I2P;

public class I2pDestinationTests
{
    private readonly byte[] _sampleHash = new byte[32];

    public I2pDestinationTests()
    {
        for (int i = 0; i < 32; i++) _sampleHash[i] = (byte)i;
    }

    [Fact]
    public void FromHash_ValidHash_CreatesDestination()
    {
        var dest = I2pDestination.FromHash(_sampleHash);
        dest.Hash.ToArray().Should().BeEquivalentTo(_sampleHash);
    }

    [Fact]
    public void FromHash_InvalidLength_Throws()
    {
        var act = () => I2pDestination.FromHash(new byte[16]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromHash_CopiesData_NotReference()
    {
        var dest = I2pDestination.FromHash(_sampleHash);
        _sampleHash[0] = 0xFF;
        dest.Hash[0].Should().Be(0x00);
    }

    [Fact]
    public void ToCompact_Returns32Bytes()
    {
        var dest = I2pDestination.FromHash(_sampleHash);
        dest.ToCompact().Length.Should().Be(32);
    }

    [Fact]
    public void FromCompact_RoundTrips()
    {
        var original = I2pDestination.FromHash(_sampleHash);
        var compact = original.ToCompact();
        var restored = I2pDestination.FromCompact(compact);
        restored.Should().Be(original);
    }

    [Fact]
    public void ToBase32_ReturnsExpectedFormat()
    {
        var dest = I2pDestination.FromHash(_sampleHash);
        var b32 = dest.ToBase32();
        b32.Should().EndWith(".b32.i2p");
        b32.Length.Should().BeGreaterThan(8);
    }

    [Fact]
    public void Equality_SameHash_AreEqual()
    {
        var a = I2pDestination.FromHash(_sampleHash);
        var b = I2pDestination.FromHash((byte[])_sampleHash.Clone());
        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentHash_AreNotEqual()
    {
        var a = I2pDestination.FromHash(_sampleHash);
        var other = (byte[])_sampleHash.Clone();
        other[0] = 0xFF;
        var b = I2pDestination.FromHash(other);
        a.Should().NotBe(b);
    }

    [Fact]
    public void WithBase64_StoresFullDestination()
    {
        var dest = I2pDestination.FromHash(_sampleHash);
        var fullBase64 = Convert.ToBase64String(new byte[387]);
        var withFull = dest.WithBase64(fullBase64);
        withFull.Base64Destination.Should().Be(fullBase64);
        withFull.Hash.ToArray().Should().BeEquivalentTo(_sampleHash);
    }

    [Fact]
    public void ToString_ReturnsTruncatedBase32()
    {
        var dest = I2pDestination.FromHash(_sampleHash);
        dest.ToString().Length.Should().BeLessThan(60);
    }
}
