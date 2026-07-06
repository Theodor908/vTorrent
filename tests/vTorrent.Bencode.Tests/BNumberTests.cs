using System.Text;
using FluentAssertions;
using vTorrent.Bencode.Objects;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class BNumberTests
{
    #region Construction & Value

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(42)]
    [InlineData(-42)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Constructor_ShouldStoreValue(long value)
    {
        var number = new BNumber(value);
        number.Value.Should().Be(value);
    }

    [Fact]
    public void ImplicitConversion_FromLong_ShouldWork()
    {
        BNumber number = 42L;
        number.Value.Should().Be(42);
    }

    [Fact]
    public void ImplicitConversion_ToLong_ShouldWork()
    {
        var number = new BNumber(42);
        long value = number;
        value.Should().Be(42);
    }

    [Fact]
    public void ImplicitConversion_FromInt_ShouldWork()
    {
        BNumber number = 42;
        number.Value.Should().Be(42);
    }

    [Fact]
    public void ImplicitConversion_ToInt_ShouldWork()
    {
        var number = new BNumber(42);
        int value = number;
        value.Should().Be(42);
    }

    #endregion

    #region Encoding

    [Theory]
    [InlineData(0, "i0e")]
    [InlineData(1, "i1e")]
    [InlineData(-1, "i-1e")]
    [InlineData(42, "i42e")]
    [InlineData(-42, "i-42e")]
    [InlineData(123456789, "i123456789e")]
    [InlineData(-123456789, "i-123456789e")]
    public void EncodeTo_ShouldProduceCorrectBencode(long value, string expected)
    {
        var number = new BNumber(value);
        var buffer = new byte[number.GetSizeInBytes()];

        var bytesWritten = number.EncodeTo(buffer);

        bytesWritten.Should().Be(expected.Length);
        Encoding.ASCII.GetString(buffer).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 3)]      // "i0e"
    [InlineData(1, 3)]      // "i1e"
    [InlineData(-1, 4)]     // "i-1e"
    [InlineData(42, 4)]     // "i42e"
    [InlineData(-42, 5)]    // "i-42e"
    [InlineData(123456789, 11)]  // "i123456789e" = 11 chars
    public void GetSizeInBytes_ShouldReturnCorrectSize(long value, int expectedSize)
    {
        var number = new BNumber(value);
        number.GetSizeInBytes().Should().Be(expectedSize);
    }

    [Fact]
    public void EncodeTo_WithTooSmallBuffer_ShouldThrow()
    {
        var number = new BNumber(12345);
        var buffer = new byte[2]; // Too small

        var act = () => number.EncodeTo(buffer);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EncodeTo_Stream_ShouldWriteCorrectly()
    {
        var number = new BNumber(42);
        using var stream = new MemoryStream();

        number.EncodeTo(stream);

        var result = Encoding.ASCII.GetString(stream.ToArray());
        result.Should().Be("i42e");
    }

    [Fact]
    public async Task EncodeToAsync_Stream_ShouldWriteCorrectly()
    {
        var number = new BNumber(42);
        using var stream = new MemoryStream();

        await number.EncodeToAsync(stream);

        var result = Encoding.ASCII.GetString(stream.ToArray());
        result.Should().Be("i42e");
    }

    #endregion

    #region Equality & Comparison

    [Fact]
    public void Equals_WithSameValue_ShouldReturnTrue()
    {
        var a = new BNumber(42);
        var b = new BNumber(42);

        a.Equals(b).Should().BeTrue();
        a.Equals((object)b).Should().BeTrue();
    }

    [Fact]
    public void Equals_WithDifferentValue_ShouldReturnFalse()
    {
        var a = new BNumber(42);
        var b = new BNumber(43);

        a.Equals(b).Should().BeFalse();
        a.Equals((object)b).Should().BeFalse();
    }

    [Fact]
    public void Equals_WithNull_ShouldReturnFalse()
    {
        var number = new BNumber(42);
        number.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void CompareTo_ShouldOrderCorrectly()
    {
        var small = new BNumber(1);
        var medium = new BNumber(5);
        var large = new BNumber(10);

        small.CompareTo(medium).Should().BeLessThan(0);
        medium.CompareTo(small).Should().BeGreaterThan(0);
        medium.CompareTo(medium).Should().Be(0);
        large.CompareTo(small).Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetHashCode_SameValues_ShouldMatch()
    {
        var a = new BNumber(42);
        var b = new BNumber(42);

        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentValues_ShouldDiffer()
    {
        var a = new BNumber(42);
        var b = new BNumber(43);

        a.GetHashCode().Should().NotBe(b.GetHashCode());
    }

    #endregion

    #region ToString

    [Theory]
    [InlineData(0, "0")]
    [InlineData(42, "42")]
    [InlineData(-42, "-42")]
    public void ToString_ShouldReturnValueAsString(long value, string expected)
    {
        var number = new BNumber(value);
        number.ToString().Should().Be(expected);
    }

    #endregion
}
