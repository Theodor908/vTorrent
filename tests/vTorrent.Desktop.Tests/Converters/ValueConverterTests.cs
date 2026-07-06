using System.Globalization;
using FluentAssertions;
using vTorrent.Desktop.Views;
using Xunit;

namespace vTorrent.Tests.Unit.Converters;

public class InfinityFormatConverterTests
{
    private readonly InfinityFormatConverter _converter = InfinityFormatConverter.Instance;
    private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

    #region Convert - Decimal

    [Fact]
    public void Convert_DecimalZero_ShouldReturnInfinity()
    {
        var result = _converter.Convert(0m, typeof(string), null, _culture);
        result.Should().Be("\u221e"); // ∞
    }

    [Fact]
    public void Convert_DecimalNonZero_ShouldReturnString()
    {
        var result = _converter.Convert(42m, typeof(string), null, _culture);
        result.Should().Be("42");
    }

    [Fact]
    public void Convert_DecimalNegative_ShouldReturnString()
    {
        var result = _converter.Convert(-10m, typeof(string), null, _culture);
        result.Should().Be("-10");
    }

    #endregion

    #region Convert - Integer

    [Fact]
    public void Convert_IntZero_ShouldReturnInfinity()
    {
        var result = _converter.Convert(0, typeof(string), null, _culture);
        result.Should().Be("\u221e");
    }

    [Fact]
    public void Convert_IntNonZero_ShouldReturnString()
    {
        var result = _converter.Convert(100, typeof(string), null, _culture);
        result.Should().Be("100");
    }

    #endregion

    #region Convert - Double

    [Fact]
    public void Convert_DoubleZero_ShouldReturnInfinity()
    {
        var result = _converter.Convert(0.0, typeof(string), null, _culture);
        result.Should().Be("\u221e");
    }

    [Fact]
    public void Convert_DoubleNonZero_ShouldReturnString()
    {
        var result = _converter.Convert(3.14, typeof(string), null, _culture);
        result.Should().Be("3.14");
    }

    #endregion

    #region Convert - Null/Other

    [Fact]
    public void Convert_Null_ShouldReturnInfinity()
    {
        var result = _converter.Convert(null, typeof(string), null, _culture);
        result.Should().Be("\u221e");
    }

    #endregion

    #region ConvertBack - To Decimal

    [Fact]
    public void ConvertBack_InfinitySymbol_ToDecimal_ShouldReturnZero()
    {
        var result = _converter.ConvertBack("\u221e", typeof(decimal), null, _culture);
        result.Should().Be(0m);
    }

    [Fact]
    public void ConvertBack_EmptyString_ToDecimal_ShouldReturnZero()
    {
        var result = _converter.ConvertBack("", typeof(decimal), null, _culture);
        result.Should().Be(0m);
    }

    [Fact]
    public void ConvertBack_Whitespace_ToDecimal_ShouldReturnZero()
    {
        var result = _converter.ConvertBack("   ", typeof(decimal), null, _culture);
        result.Should().Be(0m);
    }

    [Fact]
    public void ConvertBack_ValidNumber_ToDecimal_ShouldReturnValue()
    {
        var result = _converter.ConvertBack("42", typeof(decimal), null, _culture);
        result.Should().Be(42m);
    }

    [Fact]
    public void ConvertBack_DecimalNumber_ToDecimal_ShouldReturnValue()
    {
        var result = _converter.ConvertBack("3.14", typeof(decimal), null, _culture);
        result.Should().Be(3.14m);
    }

    #endregion

    #region ConvertBack - To Integer

    [Fact]
    public void ConvertBack_InfinitySymbol_ToInt_ShouldReturnZero()
    {
        var result = _converter.ConvertBack("\u221e", typeof(int), null, _culture);
        result.Should().Be(0);
    }

    [Fact]
    public void ConvertBack_ValidNumber_ToInt_ShouldReturnValue()
    {
        var result = _converter.ConvertBack("100", typeof(int), null, _culture);
        result.Should().Be(100);
    }

    #endregion

    #region ConvertBack - To Double

    [Fact]
    public void ConvertBack_InfinitySymbol_ToDouble_ShouldReturnZero()
    {
        var result = _converter.ConvertBack("\u221e", typeof(double), null, _culture);
        result.Should().Be(0.0);
    }

    [Fact]
    public void ConvertBack_ValidNumber_ToDouble_ShouldReturnValue()
    {
        var result = _converter.ConvertBack("3.14", typeof(double), null, _culture);
        result.Should().Be(3.14);
    }

    #endregion

    #region ConvertBack - Nullable Types

    [Fact]
    public void ConvertBack_InfinitySymbol_ToNullableDecimal_ShouldReturnZero()
    {
        var result = _converter.ConvertBack("\u221e", typeof(decimal?), null, _culture);
        result.Should().Be(0m);
    }

    [Fact]
    public void ConvertBack_InfinitySymbol_ToNullableInt_ShouldReturnZero()
    {
        var result = _converter.ConvertBack("\u221e", typeof(int?), null, _culture);
        result.Should().Be(0);
    }

    [Fact]
    public void ConvertBack_InfinitySymbol_ToNullableDouble_ShouldReturnZero()
    {
        var result = _converter.ConvertBack("\u221e", typeof(double?), null, _culture);
        result.Should().Be(0.0);
    }

    #endregion

    #region Singleton Instance

    [Fact]
    public void Instance_ShouldReturnSameInstance()
    {
        var instance1 = InfinityFormatConverter.Instance;
        var instance2 = InfinityFormatConverter.Instance;
        instance1.Should().BeSameAs(instance2);
    }

    #endregion
}

public class IntEqualConverterTests
{
    private readonly IntEqualConverter _converter = IntEqualConverter.Instance;
    private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

    #region Convert

    [Fact]
    public void Convert_EqualValues_ShouldReturnTrue()
    {
        var result = _converter.Convert(5, typeof(bool), "5", _culture);
        result.Should().Be(true);
    }

    [Fact]
    public void Convert_DifferentValues_ShouldReturnFalse()
    {
        var result = _converter.Convert(5, typeof(bool), "3", _culture);
        result.Should().Be(false);
    }

    [Fact]
    public void Convert_ZeroEqualsZero_ShouldReturnTrue()
    {
        var result = _converter.Convert(0, typeof(bool), "0", _culture);
        result.Should().Be(true);
    }

    [Fact]
    public void Convert_NegativeValues_ShouldCompareCorrectly()
    {
        var result = _converter.Convert(-1, typeof(bool), "-1", _culture);
        result.Should().Be(true);
    }

    [Fact]
    public void Convert_NullValue_ShouldReturnFalse()
    {
        var result = _converter.Convert(null, typeof(bool), "5", _culture);
        result.Should().Be(false);
    }

    [Fact]
    public void Convert_NullParameter_ShouldReturnFalse()
    {
        var result = _converter.Convert(5, typeof(bool), null, _culture);
        result.Should().Be(false);
    }

    [Fact]
    public void Convert_InvalidParameter_ShouldReturnFalse()
    {
        var result = _converter.Convert(5, typeof(bool), "not-a-number", _culture);
        result.Should().Be(false);
    }

    [Fact]
    public void Convert_NonIntValue_ShouldReturnFalse()
    {
        var result = _converter.Convert("5", typeof(bool), "5", _culture);
        result.Should().Be(false);
    }

    #endregion

    #region ConvertBack

    [Fact]
    public void ConvertBack_ShouldThrow()
    {
        var act = () => _converter.ConvertBack(true, typeof(int), "5", _culture);
        act.Should().Throw<NotImplementedException>();
    }

    #endregion

    #region Singleton Instance

    [Fact]
    public void Instance_ShouldReturnSameInstance()
    {
        var instance1 = IntEqualConverter.Instance;
        var instance2 = IntEqualConverter.Instance;
        instance1.Should().BeSameAs(instance2);
    }

    #endregion
}
