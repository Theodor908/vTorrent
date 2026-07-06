using System.Text;
using FluentAssertions;
using vTorrent.Bencode.Objects;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class BStringTests
{
    #region Construction

    [Fact]
    public void Constructor_WithString_ShouldStoreValue()
    {
        var str = new BString("hello");
        str.ToString().Should().Be("hello");
    }

    [Fact]
    public void Constructor_WithByteArray_ShouldStoreValue()
    {
        var bytes = Encoding.UTF8.GetBytes("hello");
        var str = new BString(bytes);
        str.ToString().Should().Be("hello");
    }

    [Fact]
    public void Constructor_WithSpan_ShouldStoreValue()
    {
        var bytes = Encoding.UTF8.GetBytes("hello");
        var str = new BString(bytes.AsSpan());
        str.ToString().Should().Be("hello");
    }

    [Fact]
    public void Constructor_WithNullByteArray_ShouldThrow()
    {
        var act = () => new BString((byte[])null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithNullString_ShouldThrow()
    {
        var act = () => new BString((string)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithEmptyString_ShouldWork()
    {
        var str = new BString("");
        str.ToString().Should().BeEmpty();
        str.Value.Length.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithCustomEncoding_ShouldUseIt()
    {
        var encoding = Encoding.Latin1;
        var str = new BString("hello", encoding);
        str.Encoding.Should().Be(encoding);
    }

    #endregion

    #region Implicit Conversions

    [Fact]
    public void ImplicitConversion_FromString_ShouldWork()
    {
        BString str = "hello";
        str.ToString().Should().Be("hello");
    }

    [Fact]
    public void ImplicitConversion_ToString_ShouldWork()
    {
        var bstr = new BString("hello");
        string str = bstr;
        str.Should().Be("hello");
    }

    [Fact]
    public void ImplicitConversion_ToByteArray_ShouldWork()
    {
        var bstr = new BString("hello");
        byte[] bytes = bstr;
        Encoding.UTF8.GetString(bytes).Should().Be("hello");
    }

    [Fact]
    public void ImplicitConversion_FromNullString_ShouldReturnNull()
    {
        BString str = (string)null!;
        str.Should().BeNull();
    }

    #endregion

    #region Encoding

    [Theory]
    [InlineData("", "0:")]
    [InlineData("a", "1:a")]
    [InlineData("hello", "5:hello")]
    [InlineData("hello world", "11:hello world")]
    public void EncodeTo_ShouldProduceCorrectBencode(string value, string expected)
    {
        var str = new BString(value);
        var buffer = new byte[str.GetSizeInBytes()];

        var bytesWritten = str.EncodeTo(buffer);

        bytesWritten.Should().Be(expected.Length);
        Encoding.ASCII.GetString(buffer).Should().Be(expected);
    }

    [Theory]
    [InlineData("", 2)]       // "0:"
    [InlineData("a", 3)]      // "1:a"
    [InlineData("hello", 7)]  // "5:hello"
    [InlineData("0123456789", 13)]  // "10:0123456789"
    public void GetSizeInBytes_ShouldReturnCorrectSize(string value, int expectedSize)
    {
        var str = new BString(value);
        str.GetSizeInBytes().Should().Be(expectedSize);
    }

    [Fact]
    public void EncodeTo_WithTooSmallBuffer_ShouldThrow()
    {
        var str = new BString("hello");
        var buffer = new byte[2]; // Too small

        var act = () => str.EncodeTo(buffer);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EncodeTo_Stream_ShouldWriteCorrectly()
    {
        var str = new BString("hello");
        using var stream = new MemoryStream();

        str.EncodeTo(stream);

        var result = Encoding.ASCII.GetString(stream.ToArray());
        result.Should().Be("5:hello");
    }

    [Fact]
    public async Task EncodeToAsync_Stream_ShouldWriteCorrectly()
    {
        var str = new BString("hello");
        using var stream = new MemoryStream();

        await str.EncodeToAsync(stream);

        var result = Encoding.ASCII.GetString(stream.ToArray());
        result.Should().Be("5:hello");
    }

    [Fact]
    public void EncodeTo_WithBinaryData_ShouldPreserveBytes()
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
        var str = new BString(bytes);
        var buffer = new byte[str.GetSizeInBytes()];

        str.EncodeTo(buffer);

        // Should be "5:" followed by the raw bytes
        buffer[0].Should().Be((byte)'5');
        buffer[1].Should().Be((byte)':');
        buffer[2..].Should().BeEquivalentTo(bytes);
    }

    #endregion

    #region Equality & Comparison

    [Fact]
    public void Equals_WithSameValue_ShouldReturnTrue()
    {
        var a = new BString("hello");
        var b = new BString("hello");

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Equals_WithDifferentValue_ShouldReturnFalse()
    {
        var a = new BString("hello");
        var b = new BString("world");

        a.Equals(b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Equals_WithNull_ShouldReturnFalse()
    {
        var str = new BString("hello");
        str.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void Equals_WithSameReference_ShouldReturnTrue()
    {
        var str = new BString("hello");
        str.Equals(str).Should().BeTrue();
    }

    [Fact]
    public void CompareTo_ShouldOrderLexicographically()
    {
        var a = new BString("apple");
        var b = new BString("banana");
        var c = new BString("apple");

        a.CompareTo(b).Should().BeLessThan(0);
        b.CompareTo(a).Should().BeGreaterThan(0);
        a.CompareTo(c).Should().Be(0);
    }

    [Fact]
    public void CompareTo_WithNull_ShouldReturn1()
    {
        var str = new BString("hello");
        str.CompareTo(null).Should().Be(1);
    }

    [Fact]
    public void Operators_ShouldCompareCorrectly()
    {
        var a = new BString("apple");
        var b = new BString("banana");

        (a < b).Should().BeTrue();
        (b > a).Should().BeTrue();
        (a <= a).Should().BeTrue();
        (a >= a).Should().BeTrue();
    }

    [Fact]
    public void GetHashCode_SameValues_ShouldMatch()
    {
        var a = new BString("hello");
        var b = new BString("hello");

        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    #endregion

    #region Binary Data

    [Fact]
    public void BinaryData_ShouldPreserveExactBytes()
    {
        var original = new byte[] { 0x00, 0x7F, 0x80, 0xFF };
        var str = new BString(original);

        str.Value.ToArray().Should().BeEquivalentTo(original);
    }

    [Fact]
    public void BinaryData_Equality_ShouldCompareBytes()
    {
        var bytes1 = new byte[] { 0x00, 0x01, 0x02 };
        var bytes2 = new byte[] { 0x00, 0x01, 0x02 };
        var bytes3 = new byte[] { 0x00, 0x01, 0x03 };

        var a = new BString(bytes1);
        var b = new BString(bytes2);
        var c = new BString(bytes3);

        a.Equals(b).Should().BeTrue();
        a.Equals(c).Should().BeFalse();
    }

    #endregion

    #region Unicode

    [Fact]
    public void Unicode_ShouldEncodeCorrectly()
    {
        var str = new BString("日本語"); // Japanese characters
        var buffer = new byte[str.GetSizeInBytes()];

        str.EncodeTo(buffer);

        // UTF-8: 日本語 = 9 bytes
        var utf8Bytes = Encoding.UTF8.GetBytes("日本語");
        buffer.Should().StartWith(Encoding.ASCII.GetBytes($"{utf8Bytes.Length}:"));
    }

    [Fact]
    public void Unicode_ShouldRoundTrip()
    {
        var original = "Привет мир 你好世界 🌍";
        var str = new BString(original);

        str.ToString().Should().Be(original);
    }

    #endregion
}
