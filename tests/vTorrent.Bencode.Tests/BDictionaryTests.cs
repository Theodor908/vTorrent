using System.Text;
using FluentAssertions;
using vTorrent.Bencode.Objects;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class BDictionaryTests
{
    #region Construction

    [Fact]
    public void DefaultConstructor_ShouldCreateEmptyDictionary()
    {
        var dict = new BDictionary();
        dict.Count.Should().Be(0);
    }

    #endregion

    #region Add Operations

    [Fact]
    public void Add_WithBStringKey_ShouldStoreValue()
    {
        var dict = new BDictionary();
        var key = new BString("key");
        var value = new BNumber(42);

        dict.Add(key, value);

        dict.Count.Should().Be(1);
        dict[key].Should().Be(value);
    }

    [Fact]
    public void Add_WithStringKey_ShouldStoreValue()
    {
        var dict = new BDictionary();
        dict.Add("key", new BNumber(42));

        dict.Count.Should().Be(1);
        ((BNumber)dict["key"]).Value.Should().Be(42);
    }

    [Fact]
    public void AddString_ShouldAddBString()
    {
        var dict = new BDictionary();
        dict.AddString("greeting", "hello");

        ((BString)dict["greeting"]).ToString().Should().Be("hello");
    }

    [Fact]
    public void AddNumber_ShouldAddBNumber()
    {
        var dict = new BDictionary();
        dict.AddNumber("answer", 42);

        ((BNumber)dict["answer"]).Value.Should().Be(42);
    }

    [Fact]
    public void AddBytes_ShouldAddBString()
    {
        var dict = new BDictionary();
        var bytes = new byte[] { 1, 2, 3 };
        dict.AddBytes("data", bytes);

        dict["data"].Should().BeOfType<BString>();
    }

    #endregion

    #region Get Operations

    [Fact]
    public void Get_WithValidKey_ShouldReturnTypedValue()
    {
        var dict = new BDictionary();
        dict.Add("number", new BNumber(42));

        var number = dict.Get<BNumber>("number");

        number.Value.Should().Be(42);
    }

    [Fact]
    public void Get_WithInvalidKey_ShouldThrow()
    {
        var dict = new BDictionary();
        var act = () => dict.Get<BNumber>("missing");
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Get_WithWrongType_ShouldThrow()
    {
        var dict = new BDictionary();
        dict.Add("number", new BNumber(42));

        var act = () => dict.Get<BString>("number");

        act.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void GetOrDefault_WithValidKey_ShouldReturnValue()
    {
        var dict = new BDictionary();
        dict.Add("number", new BNumber(42));

        var number = dict.GetOrDefault<BNumber>("number");

        number!.Value.Should().Be(42);
    }

    [Fact]
    public void GetOrDefault_WithMissingKey_ShouldReturnDefault()
    {
        var dict = new BDictionary();
        var result = dict.GetOrDefault<BNumber>("missing");
        result.Should().BeNull();
    }

    [Fact]
    public void GetString_ShouldReturnStringValue()
    {
        var dict = new BDictionary();
        dict.AddString("name", "test");

        dict.GetString("name").Should().Be("test");
    }

    [Fact]
    public void GetStringOrDefault_WithMissingKey_ShouldReturnDefault()
    {
        var dict = new BDictionary();
        dict.GetStringOrDefault("missing", "default").Should().Be("default");
    }

    [Fact]
    public void GetNumber_ShouldReturnLongValue()
    {
        var dict = new BDictionary();
        dict.AddNumber("count", 100);

        dict.GetNumber("count").Should().Be(100);
    }

    [Fact]
    public void GetNumberOrDefault_WithMissingKey_ShouldReturnDefault()
    {
        var dict = new BDictionary();
        dict.GetNumberOrDefault("missing", 42).Should().Be(42);
    }

    [Fact]
    public void GetList_ShouldReturnBList()
    {
        var dict = new BDictionary();
        var list = new BList();
        list.Add(new BNumber(1));
        dict.Add("items", list);

        var result = dict.GetList("items");

        result.Count.Should().Be(1);
    }

    [Fact]
    public void GetDictionary_ShouldReturnBDictionary()
    {
        var dict = new BDictionary();
        var inner = new BDictionary();
        inner.AddNumber("value", 42);
        dict.Add("nested", inner);

        var result = dict.GetDictionary("nested");

        result.GetNumber("value").Should().Be(42);
    }

    #endregion

    #region Dictionary Operations

    [Fact]
    public void Indexer_Set_ShouldUpdateValue()
    {
        var dict = new BDictionary();
        dict["key"] = new BNumber(1);
        dict["key"] = new BNumber(2);

        ((BNumber)dict["key"]).Value.Should().Be(2);
    }

    [Fact]
    public void Indexer_SetNull_ShouldThrow()
    {
        var dict = new BDictionary();
        var act = () => dict["key"] = null!;
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TryGetValue_WithExistingKey_ShouldReturnTrue()
    {
        var dict = new BDictionary();
        dict.Add("key", new BNumber(42));

        dict.TryGetValue("key", out var value).Should().BeTrue();
        ((BNumber)value!).Value.Should().Be(42);
    }

    [Fact]
    public void TryGetValue_WithMissingKey_ShouldReturnFalse()
    {
        var dict = new BDictionary();
        dict.TryGetValue("missing", out _).Should().BeFalse();
    }

    [Fact]
    public void ContainsKey_WithExistingKey_ShouldReturnTrue()
    {
        var dict = new BDictionary();
        dict.Add("key", new BNumber(42));

        dict.ContainsKey("key").Should().BeTrue();
    }

    [Fact]
    public void ContainsKey_WithMissingKey_ShouldReturnFalse()
    {
        var dict = new BDictionary();
        dict.ContainsKey("missing").Should().BeFalse();
    }

    [Fact]
    public void Remove_ShouldRemoveKey()
    {
        var dict = new BDictionary();
        dict.Add("key", new BNumber(42));

        dict.Remove("key").Should().BeTrue();
        dict.Count.Should().Be(0);
    }

    [Fact]
    public void Clear_ShouldRemoveAllEntries()
    {
        var dict = new BDictionary();
        dict.Add("a", new BNumber(1));
        dict.Add("b", new BNumber(2));

        dict.Clear();

        dict.Count.Should().Be(0);
    }

    [Fact]
    public void Keys_ShouldReturnAllKeys()
    {
        var dict = new BDictionary();
        dict.Add("a", new BNumber(1));
        dict.Add("b", new BNumber(2));

        var keys = dict.Keys.Select(k => k.ToString()).ToList();

        keys.Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public void Values_ShouldReturnAllValues()
    {
        var dict = new BDictionary();
        dict.Add("a", new BNumber(1));
        dict.Add("b", new BNumber(2));

        var values = dict.Values.Cast<BNumber>().Select(n => n.Value).ToList();

        values.Should().BeEquivalentTo(new[] { 1L, 2L });
    }

    #endregion

    #region MergeWith

    [Fact]
    public void MergeWith_ShouldAddNewKeys()
    {
        var dict1 = new BDictionary();
        dict1.Add("a", new BNumber(1));

        var dict2 = new BDictionary();
        dict2.Add("b", new BNumber(2));

        dict1.MergeWith(dict2);

        dict1.Count.Should().Be(2);
        dict1.GetNumber("a").Should().Be(1);
        dict1.GetNumber("b").Should().Be(2);
    }

    [Fact]
    public void MergeWith_WithOverwrite_ShouldReplaceExisting()
    {
        var dict1 = new BDictionary();
        dict1.Add("key", new BNumber(1));

        var dict2 = new BDictionary();
        dict2.Add("key", new BNumber(2));

        dict1.MergeWith(dict2, overwrite: true);

        dict1.GetNumber("key").Should().Be(2);
    }

    [Fact]
    public void MergeWith_WithoutOverwrite_ShouldKeepExisting()
    {
        var dict1 = new BDictionary();
        dict1.Add("key", new BNumber(1));

        var dict2 = new BDictionary();
        dict2.Add("key", new BNumber(2));

        dict1.MergeWith(dict2, overwrite: false);

        dict1.GetNumber("key").Should().Be(1);
    }

    [Fact]
    public void MergeWith_Null_ShouldThrow()
    {
        var dict = new BDictionary();
        var act = () => dict.MergeWith(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Encoding

    [Fact]
    public void EncodeTo_EmptyDictionary_ShouldProduceDE()
    {
        var dict = new BDictionary();
        var buffer = new byte[dict.GetSizeInBytes()];

        dict.EncodeTo(buffer);

        Encoding.ASCII.GetString(buffer).Should().Be("de");
    }

    [Fact]
    public void EncodeTo_SingleEntry_ShouldProduceCorrectBencode()
    {
        var dict = new BDictionary();
        dict.Add("key", new BNumber(42));

        var buffer = new byte[dict.GetSizeInBytes()];
        dict.EncodeTo(buffer);

        Encoding.ASCII.GetString(buffer).Should().Be("d3:keyi42ee");
    }

    [Fact]
    public void EncodeTo_MultipleEntries_ShouldBeSorted()
    {
        var dict = new BDictionary();
        dict.Add("zebra", new BNumber(3));
        dict.Add("apple", new BNumber(1));
        dict.Add("banana", new BNumber(2));

        var buffer = new byte[dict.GetSizeInBytes()];
        dict.EncodeTo(buffer);

        // Keys should be sorted lexicographically
        Encoding.ASCII.GetString(buffer).Should().Be("d5:applei1e6:bananai2e5:zebrai3ee");
    }

    [Fact]
    public void EncodeTo_NestedDictionary_ShouldProduceCorrectBencode()
    {
        var inner = new BDictionary();
        inner.Add("value", new BNumber(42));

        var outer = new BDictionary();
        outer.Add("nested", inner);

        var buffer = new byte[outer.GetSizeInBytes()];
        outer.EncodeTo(buffer);

        Encoding.ASCII.GetString(buffer).Should().Be("d6:nestedd5:valuei42eee");
    }

    [Fact]
    public void EncodeTo_WithList_ShouldProduceCorrectBencode()
    {
        var list = new BList();
        list.Add(new BNumber(1));
        list.Add(new BNumber(2));

        var dict = new BDictionary();
        dict.Add("items", list);

        var buffer = new byte[dict.GetSizeInBytes()];
        dict.EncodeTo(buffer);

        Encoding.ASCII.GetString(buffer).Should().Be("d5:itemsli1ei2eee");
    }

    [Theory]
    [InlineData(0, 2)]   // "de"
    public void GetSizeInBytes_EmptyDict_ShouldReturn2(int _, int expectedSize)
    {
        var dict = new BDictionary();
        dict.GetSizeInBytes().Should().Be(expectedSize);
    }

    [Fact]
    public void EncodeTo_Stream_ShouldWriteCorrectly()
    {
        var dict = new BDictionary();
        dict.Add("key", new BNumber(42));

        using var stream = new MemoryStream();
        dict.EncodeTo(stream);

        Encoding.ASCII.GetString(stream.ToArray()).Should().Be("d3:keyi42ee");
    }

    [Fact]
    public async Task EncodeToAsync_Stream_ShouldWriteCorrectly()
    {
        var dict = new BDictionary();
        dict.Add("key", new BNumber(42));

        using var stream = new MemoryStream();
        await dict.EncodeToAsync(stream);

        Encoding.ASCII.GetString(stream.ToArray()).Should().Be("d3:keyi42ee");
    }

    #endregion

    #region Enumeration

    [Fact]
    public void Enumeration_ShouldIterateAllEntries()
    {
        var dict = new BDictionary();
        dict.Add("a", new BNumber(1));
        dict.Add("b", new BNumber(2));

        var entries = dict.ToList();

        entries.Should().HaveCount(2);
    }

    #endregion

    #region ToString

    [Fact]
    public void ToString_ShouldShowCount()
    {
        var dict = new BDictionary();
        dict.Add("a", new BNumber(1));
        dict.Add("b", new BNumber(2));

        dict.ToString().Should().Be("BDictionary[2]");
    }

    #endregion
}
