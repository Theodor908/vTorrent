using System.Text;
using FluentAssertions;
using vTorrent.Bencode.Objects;
using Xunit;

namespace vTorrent.Tests.Unit.Bencode;

public class BListTests
{
    #region Construction

    [Fact]
    public void DefaultConstructor_ShouldCreateEmptyList()
    {
        var list = new BList();
        list.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithCapacity_ShouldCreateEmptyList()
    {
        var list = new BList(10);
        list.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithItems_ShouldContainItems()
    {
        var items = new IBObject[] { new BNumber(1), new BString("hello") };
        var list = new BList(items);

        list.Count.Should().Be(2);
        list[0].Should().Be(items[0]);
        list[1].Should().Be(items[1]);
    }

    #endregion

    #region Add Operations

    [Fact]
    public void Add_ShouldAppendItem()
    {
        var list = new BList();
        var number = new BNumber(42);

        list.Add(number);

        list.Count.Should().Be(1);
        list[0].Should().Be(number);
    }

    [Fact]
    public void Add_NullItem_ShouldThrow()
    {
        var list = new BList();
        var act = () => list.Add(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddString_ShouldAddBString()
    {
        var list = new BList();
        list.AddString("hello");

        list.Count.Should().Be(1);
        list[0].Should().BeOfType<BString>();
        ((BString)list[0]).ToString().Should().Be("hello");
    }

    [Fact]
    public void AddNumber_ShouldAddBNumber()
    {
        var list = new BList();
        list.AddNumber(42);

        list.Count.Should().Be(1);
        list[0].Should().BeOfType<BNumber>();
        ((BNumber)list[0]).Value.Should().Be(42);
    }

    [Fact]
    public void AddBytes_ShouldAddBString()
    {
        var list = new BList();
        var bytes = new byte[] { 1, 2, 3 };
        list.AddBytes(bytes);

        list.Count.Should().Be(1);
        list[0].Should().BeOfType<BString>();
    }

    [Fact]
    public void AddRange_ShouldAddMultipleItems()
    {
        var list = new BList();
        var items = new IBObject[] { new BNumber(1), new BNumber(2), new BNumber(3) };

        list.AddRange(items);

        list.Count.Should().Be(3);
    }

    #endregion

    #region Get Operations

    [Fact]
    public void Get_WithValidIndex_ShouldReturnTypedItem()
    {
        var list = new BList();
        list.Add(new BNumber(42));

        var number = list.Get<BNumber>(0);

        number.Value.Should().Be(42);
    }

    [Fact]
    public void Get_WithInvalidType_ShouldThrow()
    {
        var list = new BList();
        list.Add(new BNumber(42));

        var act = () => list.Get<BString>(0);

        act.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void Get_WithInvalidIndex_ShouldThrow()
    {
        var list = new BList();
        var act = () => list.Get<BNumber>(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetAll_ShouldReturnOnlyMatchingTypes()
    {
        var list = new BList();
        list.Add(new BNumber(1));
        list.Add(new BString("hello"));
        list.Add(new BNumber(2));
        list.Add(new BString("world"));

        var numbers = list.GetAll<BNumber>().ToList();

        numbers.Should().HaveCount(2);
        numbers[0].Value.Should().Be(1);
        numbers[1].Value.Should().Be(2);
    }

    #endregion

    #region List Operations

    [Fact]
    public void Indexer_Set_ShouldUpdateItem()
    {
        var list = new BList();
        list.Add(new BNumber(1));

        list[0] = new BNumber(42);

        ((BNumber)list[0]).Value.Should().Be(42);
    }

    [Fact]
    public void Indexer_SetNull_ShouldThrow()
    {
        var list = new BList();
        list.Add(new BNumber(1));

        var act = () => list[0] = null!;

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Remove_ShouldRemoveItem()
    {
        var list = new BList();
        var number = new BNumber(42);
        list.Add(number);

        list.Remove(number).Should().BeTrue();
        list.Count.Should().Be(0);
    }

    [Fact]
    public void RemoveAt_ShouldRemoveAtIndex()
    {
        var list = new BList();
        list.Add(new BNumber(1));
        list.Add(new BNumber(2));
        list.Add(new BNumber(3));

        list.RemoveAt(1);

        list.Count.Should().Be(2);
        ((BNumber)list[0]).Value.Should().Be(1);
        ((BNumber)list[1]).Value.Should().Be(3);
    }

    [Fact]
    public void Clear_ShouldRemoveAllItems()
    {
        var list = new BList();
        list.Add(new BNumber(1));
        list.Add(new BNumber(2));

        list.Clear();

        list.Count.Should().Be(0);
    }

    [Fact]
    public void Contains_ShouldReturnTrueForExistingItem()
    {
        var list = new BList();
        var number = new BNumber(42);
        list.Add(number);

        list.Contains(number).Should().BeTrue();
    }

    [Fact]
    public void IndexOf_ShouldReturnCorrectIndex()
    {
        var list = new BList();
        var number = new BNumber(42);
        list.Add(new BNumber(1));
        list.Add(number);

        list.IndexOf(number).Should().Be(1);
    }

    [Fact]
    public void Insert_ShouldInsertAtPosition()
    {
        var list = new BList();
        list.Add(new BNumber(1));
        list.Add(new BNumber(3));

        list.Insert(1, new BNumber(2));

        list.Count.Should().Be(3);
        ((BNumber)list[1]).Value.Should().Be(2);
    }

    #endregion

    #region Encoding

    [Fact]
    public void EncodeTo_EmptyList_ShouldProduceLE()
    {
        var list = new BList();
        var buffer = new byte[list.GetSizeInBytes()];

        list.EncodeTo(buffer);

        Encoding.ASCII.GetString(buffer).Should().Be("le");
    }

    [Fact]
    public void EncodeTo_WithNumbers_ShouldProduceCorrectBencode()
    {
        var list = new BList();
        list.Add(new BNumber(1));
        list.Add(new BNumber(2));
        list.Add(new BNumber(3));

        var buffer = new byte[list.GetSizeInBytes()];
        list.EncodeTo(buffer);

        Encoding.ASCII.GetString(buffer).Should().Be("li1ei2ei3ee");
    }

    [Fact]
    public void EncodeTo_WithMixedTypes_ShouldProduceCorrectBencode()
    {
        var list = new BList();
        list.Add(new BNumber(42));
        list.Add(new BString("hello"));

        var buffer = new byte[list.GetSizeInBytes()];
        list.EncodeTo(buffer);

        Encoding.ASCII.GetString(buffer).Should().Be("li42e5:helloe");
    }

    [Fact]
    public void EncodeTo_NestedList_ShouldProduceCorrectBencode()
    {
        var innerList = new BList();
        innerList.Add(new BNumber(1));
        innerList.Add(new BNumber(2));

        var outerList = new BList();
        outerList.Add(innerList);
        outerList.Add(new BNumber(3));

        var buffer = new byte[outerList.GetSizeInBytes()];
        outerList.EncodeTo(buffer);

        Encoding.ASCII.GetString(buffer).Should().Be("lli1ei2eei3ee");
    }

    [Theory]
    [InlineData(0, 2)]   // "le"
    [InlineData(1, 5)]   // "li0ee" with BNumber(0)
    public void GetSizeInBytes_ShouldReturnCorrectSize(int itemCount, int expectedSize)
    {
        var list = new BList();
        for (int i = 0; i < itemCount; i++)
            list.Add(new BNumber(0));

        list.GetSizeInBytes().Should().Be(expectedSize);
    }

    [Fact]
    public void EncodeTo_Stream_ShouldWriteCorrectly()
    {
        var list = new BList();
        list.Add(new BNumber(42));

        using var stream = new MemoryStream();
        list.EncodeTo(stream);

        Encoding.ASCII.GetString(stream.ToArray()).Should().Be("li42ee");
    }

    [Fact]
    public async Task EncodeToAsync_Stream_ShouldWriteCorrectly()
    {
        var list = new BList();
        list.Add(new BNumber(42));

        using var stream = new MemoryStream();
        await list.EncodeToAsync(stream);

        Encoding.ASCII.GetString(stream.ToArray()).Should().Be("li42ee");
    }

    #endregion

    #region Enumeration

    [Fact]
    public void Enumeration_ShouldIterateAllItems()
    {
        var list = new BList();
        list.Add(new BNumber(1));
        list.Add(new BNumber(2));
        list.Add(new BNumber(3));

        var values = list.Cast<BNumber>().Select(n => n.Value).ToList();

        values.Should().BeEquivalentTo(new[] { 1L, 2L, 3L });
    }

    #endregion

    #region ToString

    [Fact]
    public void ToString_ShouldShowCount()
    {
        var list = new BList();
        list.Add(new BNumber(1));
        list.Add(new BNumber(2));

        list.ToString().Should().Be("BList[2]");
    }

    #endregion
}
