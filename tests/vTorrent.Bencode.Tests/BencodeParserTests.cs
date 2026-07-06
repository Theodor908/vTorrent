using System.Text;
using FluentAssertions;
using vTorrent.Bencode.Exceptions;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class BencodeParserTests
{
    private readonly BencodeParser _parser = new();

    #region Number Parsing

    [Theory]
    [InlineData("i0e", 0)]
    [InlineData("i1e", 1)]
    [InlineData("i42e", 42)]
    [InlineData("i-1e", -1)]
    [InlineData("i-42e", -42)]
    [InlineData("i123456789e", 123456789)]
    [InlineData("i-123456789e", -123456789)]
    public void Parse_Integer_ShouldReturnCorrectValue(string bencode, long expected)
    {
        var data = Encoding.ASCII.GetBytes(bencode);

        var result = _parser.Parse(data, out var bytesConsumed);

        result.Should().BeOfType<BNumber>();
        ((BNumber)result).Value.Should().Be(expected);
        bytesConsumed.Should().Be(bencode.Length);
    }

    [Fact]
    public void Parse_LargeInteger_ShouldWork()
    {
        var bencode = $"i{long.MaxValue}e";
        var data = Encoding.ASCII.GetBytes(bencode);

        var result = _parser.Parse(data, out _);

        ((BNumber)result).Value.Should().Be(long.MaxValue);
    }

    #endregion

    #region String Parsing

    [Theory]
    [InlineData("0:", "")]
    [InlineData("1:a", "a")]
    [InlineData("5:hello", "hello")]
    [InlineData("11:hello world", "hello world")]
    public void Parse_String_ShouldReturnCorrectValue(string bencode, string expected)
    {
        var data = Encoding.ASCII.GetBytes(bencode);

        var result = _parser.Parse(data, out var bytesConsumed);

        result.Should().BeOfType<BString>();
        ((BString)result).ToString().Should().Be(expected);
        bytesConsumed.Should().Be(bencode.Length);
    }

    [Fact]
    public void Parse_StringWithBinaryData_ShouldPreserveBytes()
    {
        // "5:" followed by binary bytes
        var bytes = new byte[] { (byte)'5', (byte)':', 0x00, 0x01, 0x02, 0xFF, 0xFE };

        var result = _parser.Parse(bytes, out _);

        result.Should().BeOfType<BString>();
        ((BString)result).Value.ToArray().Should().BeEquivalentTo(new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE });
    }

    [Fact]
    public void Parse_UnicodeString_ShouldWork()
    {
        var text = "日本語";
        var utf8Bytes = Encoding.UTF8.GetBytes(text);
        var bencode = $"{utf8Bytes.Length}:".Select(c => (byte)c).Concat(utf8Bytes).ToArray();

        var result = _parser.Parse(bencode, out _);

        ((BString)result).ToString().Should().Be(text);
    }

    #endregion

    #region List Parsing

    [Fact]
    public void Parse_EmptyList_ShouldReturnEmptyBList()
    {
        var data = Encoding.ASCII.GetBytes("le");

        var result = _parser.Parse(data, out var bytesConsumed);

        result.Should().BeOfType<BList>();
        ((BList)result).Count.Should().Be(0);
        bytesConsumed.Should().Be(2);
    }

    [Fact]
    public void Parse_ListWithNumbers_ShouldReturnCorrectList()
    {
        var data = Encoding.ASCII.GetBytes("li1ei2ei3ee");

        var result = _parser.Parse(data, out _);

        result.Should().BeOfType<BList>();
        var list = (BList)result;
        list.Count.Should().Be(3);
        ((BNumber)list[0]).Value.Should().Be(1);
        ((BNumber)list[1]).Value.Should().Be(2);
        ((BNumber)list[2]).Value.Should().Be(3);
    }

    [Fact]
    public void Parse_ListWithStrings_ShouldReturnCorrectList()
    {
        var data = Encoding.ASCII.GetBytes("l5:hello5:worlde");

        var result = _parser.Parse(data, out _);

        var list = (BList)result;
        list.Count.Should().Be(2);
        ((BString)list[0]).ToString().Should().Be("hello");
        ((BString)list[1]).ToString().Should().Be("world");
    }

    [Fact]
    public void Parse_ListWithMixedTypes_ShouldReturnCorrectList()
    {
        var data = Encoding.ASCII.GetBytes("li42e5:helloe");

        var result = _parser.Parse(data, out _);

        var list = (BList)result;
        list.Count.Should().Be(2);
        ((BNumber)list[0]).Value.Should().Be(42);
        ((BString)list[1]).ToString().Should().Be("hello");
    }

    [Fact]
    public void Parse_NestedLists_ShouldReturnCorrectStructure()
    {
        var data = Encoding.ASCII.GetBytes("lli1ei2eeli3ei4eee");

        var result = _parser.Parse(data, out _);

        var outer = (BList)result;
        outer.Count.Should().Be(2);

        var inner1 = (BList)outer[0];
        inner1.Count.Should().Be(2);
        ((BNumber)inner1[0]).Value.Should().Be(1);
        ((BNumber)inner1[1]).Value.Should().Be(2);

        var inner2 = (BList)outer[1];
        inner2.Count.Should().Be(2);
        ((BNumber)inner2[0]).Value.Should().Be(3);
        ((BNumber)inner2[1]).Value.Should().Be(4);
    }

    #endregion

    #region Dictionary Parsing

    [Fact]
    public void Parse_EmptyDictionary_ShouldReturnEmptyBDictionary()
    {
        var data = Encoding.ASCII.GetBytes("de");

        var result = _parser.Parse(data, out var bytesConsumed);

        result.Should().BeOfType<BDictionary>();
        ((BDictionary)result).Count.Should().Be(0);
        bytesConsumed.Should().Be(2);
    }

    [Fact]
    public void Parse_DictionaryWithNumber_ShouldReturnCorrectDictionary()
    {
        var data = Encoding.ASCII.GetBytes("d3:keyi42ee");

        var result = _parser.Parse(data, out _);

        result.Should().BeOfType<BDictionary>();
        var dict = (BDictionary)result;
        dict.Count.Should().Be(1);
        dict.GetNumber("key").Should().Be(42);
    }

    [Fact]
    public void Parse_DictionaryWithString_ShouldReturnCorrectDictionary()
    {
        var data = Encoding.ASCII.GetBytes("d4:name5:Alicee");

        var result = _parser.Parse(data, out _);

        var dict = (BDictionary)result;
        dict.GetString("name").Should().Be("Alice");
    }

    [Fact]
    public void Parse_DictionaryWithMultipleEntries_ShouldReturnCorrectDictionary()
    {
        var data = Encoding.ASCII.GetBytes("d5:applei1e6:bananai2e5:zebrai3ee");

        var result = _parser.Parse(data, out _);

        var dict = (BDictionary)result;
        dict.Count.Should().Be(3);
        dict.GetNumber("apple").Should().Be(1);
        dict.GetNumber("banana").Should().Be(2);
        dict.GetNumber("zebra").Should().Be(3);
    }

    [Fact]
    public void Parse_NestedDictionary_ShouldReturnCorrectStructure()
    {
        var data = Encoding.ASCII.GetBytes("d6:nestedd5:valuei42eee");

        var result = _parser.Parse(data, out _);

        var outer = (BDictionary)result;
        var inner = outer.GetDictionary("nested");
        inner.GetNumber("value").Should().Be(42);
    }

    [Fact]
    public void Parse_DictionaryWithList_ShouldReturnCorrectStructure()
    {
        var data = Encoding.ASCII.GetBytes("d5:itemsli1ei2ei3eee");

        var result = _parser.Parse(data, out _);

        var dict = (BDictionary)result;
        var list = dict.GetList("items");
        list.Count.Should().Be(3);
    }

    #endregion

    #region Complex Structures

    [Fact]
    public void Parse_TorrentLikeStructure_ShouldWork()
    {
        // Simplified torrent-like structure
        var dict = new BDictionary();
        dict.AddString("announce", "http://tracker.example.com");
        dict.AddNumber("creation date", 1234567890);

        var info = new BDictionary();
        info.AddString("name", "test.txt");
        info.AddNumber("length", 12345);
        info.AddNumber("piece length", 262144);
        dict.Add("info", info);

        var buffer = new byte[dict.GetSizeInBytes()];
        dict.EncodeTo(buffer);

        // Parse it back
        var result = _parser.Parse(buffer, out _);

        var parsed = (BDictionary)result;
        parsed.GetString("announce").Should().Be("http://tracker.example.com");
        parsed.GetNumber("creation date").Should().Be(1234567890);

        var parsedInfo = parsed.GetDictionary("info");
        parsedInfo.GetString("name").Should().Be("test.txt");
        parsedInfo.GetNumber("length").Should().Be(12345);
        parsedInfo.GetNumber("piece length").Should().Be(262144);
    }

    #endregion

    #region Round-Trip Tests

    [Fact]
    public void RoundTrip_Number_ShouldPreserveValue()
    {
        var original = new BNumber(123456789);
        var buffer = new byte[original.GetSizeInBytes()];
        original.EncodeTo(buffer);

        var parsed = (BNumber)_parser.Parse(buffer, out _);

        parsed.Value.Should().Be(original.Value);
    }

    [Fact]
    public void RoundTrip_String_ShouldPreserveValue()
    {
        var original = new BString("hello world");
        var buffer = new byte[original.GetSizeInBytes()];
        original.EncodeTo(buffer);

        var parsed = (BString)_parser.Parse(buffer, out _);

        parsed.ToString().Should().Be(original.ToString());
    }

    [Fact]
    public void RoundTrip_List_ShouldPreserveStructure()
    {
        var original = new BList();
        original.Add(new BNumber(1));
        original.Add(new BString("test"));
        original.Add(new BNumber(2));

        var buffer = new byte[original.GetSizeInBytes()];
        original.EncodeTo(buffer);

        var parsed = (BList)_parser.Parse(buffer, out _);

        parsed.Count.Should().Be(3);
        ((BNumber)parsed[0]).Value.Should().Be(1);
        ((BString)parsed[1]).ToString().Should().Be("test");
        ((BNumber)parsed[2]).Value.Should().Be(2);
    }

    [Fact]
    public void RoundTrip_Dictionary_ShouldPreserveStructure()
    {
        var original = new BDictionary();
        original.AddNumber("number", 42);
        original.AddString("string", "hello");

        var list = new BList();
        list.Add(new BNumber(1));
        original.Add("list", list);

        var buffer = new byte[original.GetSizeInBytes()];
        original.EncodeTo(buffer);

        var parsed = (BDictionary)_parser.Parse(buffer, out _);

        parsed.GetNumber("number").Should().Be(42);
        parsed.GetString("string").Should().Be("hello");
        parsed.GetList("list").Count.Should().Be(1);
    }

    #endregion

    #region Error Handling

    [Fact]
    public void Parse_EmptyData_ShouldThrow()
    {
        var act = () => _parser.Parse(Array.Empty<byte>(), out _);
        act.Should().Throw<InvalidBencodeException>();
    }

    [Fact]
    public void Parse_InvalidStartCharacter_ShouldThrow()
    {
        var data = Encoding.ASCII.GetBytes("x");
        var act = () => _parser.Parse(data, out _);
        act.Should().Throw<InvalidBencodeException>();
    }

    [Fact]
    public void Parse_MalformedInteger_ShouldThrow()
    {
        var data = Encoding.ASCII.GetBytes("iabce");
        var act = () => _parser.Parse(data, out _);
        act.Should().Throw<InvalidBencodeException>();
    }

    [Fact]
    public void Parse_MalformedStringLength_ShouldThrow()
    {
        var data = Encoding.ASCII.GetBytes("abc:test");
        var act = () => _parser.Parse(data, out _);
        act.Should().Throw<InvalidBencodeException>();
    }

    [Fact]
    public void Parse_DictionaryWithNonStringKey_ShouldThrow()
    {
        // Dictionary with integer key (invalid)
        var data = Encoding.ASCII.GetBytes("di42e5:valuee");
        var act = () => _parser.Parse(data, out _);
        act.Should().Throw<InvalidBencodeException>();
    }

    #endregion

    #region Bytes Consumed

    [Fact]
    public void Parse_ShouldReportCorrectBytesConsumed()
    {
        var bencode = "d3:keyi42ee";
        var extraData = "extra";
        var data = Encoding.ASCII.GetBytes(bencode + extraData);

        _parser.Parse(data, out var bytesConsumed);

        bytesConsumed.Should().Be(bencode.Length);
    }

    [Fact]
    public void Parse_WithTrailingData_ShouldOnlyConsumeValidBencode()
    {
        var data = Encoding.ASCII.GetBytes("i42egarbage");

        var result = _parser.Parse(data, out var bytesConsumed);

        ((BNumber)result).Value.Should().Be(42);
        bytesConsumed.Should().Be(4); // "i42e" = 4 bytes
    }

    #endregion

    #region Encoding Parameter

    [Fact]
    public void Constructor_WithCustomEncoding_ShouldUseIt()
    {
        var parser = new BencodeParser(Encoding.Latin1);
        parser.Encoding.Should().Be(Encoding.Latin1);
    }

    [Fact]
    public void Constructor_WithNullEncoding_ShouldUseUtf8()
    {
        var parser = new BencodeParser(null);
        parser.Encoding.Should().Be(Encoding.UTF8);
    }

    #endregion
}
