using System.Globalization;
using FluentAssertions;
using vTorrent.Desktop.Views.Converters;
using Xunit;

namespace vTorrent.Tests.Unit.Converters;

public class NullableNumericConverterTests
{
    private readonly NullableNumericConverter _converter = NullableNumericConverter.Instance;
    private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

    [Fact]
    public void Convert_Int_ShouldReturnDecimal()
    {
        var result = _converter.Convert(500, typeof(decimal?), null, _culture);
        result.Should().Be(500m);
    }

    [Fact]
    public void Convert_Float_ShouldReturnDecimal()
    {
        var result = _converter.Convert(2.5f, typeof(decimal?), null, _culture);
        result.Should().Be(2.5m);
    }

    [Fact]
    public void Convert_Zero_ShouldReturnZeroDecimal()
    {
        var result = _converter.Convert(0, typeof(decimal?), null, _culture);
        result.Should().Be(0m);
    }

    [Fact]
    public void ConvertBack_ValidDecimal_ToInt_ShouldReturnInt()
    {
        var result = _converter.ConvertBack(500m, typeof(int), "0", _culture);
        result.Should().Be(500);
    }

    [Fact]
    public void ConvertBack_Null_ToInt_ShouldReturnFallback()
    {
        var result = _converter.ConvertBack(null, typeof(int), "100", _culture);
        result.Should().Be(100);
    }

    [Fact]
    public void ConvertBack_Null_ToInt_NoFallback_ShouldReturnZero()
    {
        var result = _converter.ConvertBack(null, typeof(int), null, _culture);
        result.Should().Be(0);
    }

    [Fact]
    public void ConvertBack_Null_ToFloat_ShouldReturnFallback()
    {
        var result = _converter.ConvertBack(null, typeof(float), "1.0", _culture);
        result.Should().BeOfType<float>().And.Be(1.0f);
    }

    [Fact]
    public void ConvertBack_ValidDecimal_ToFloat_ShouldReturnFloat()
    {
        var result = _converter.ConvertBack(2.5m, typeof(float), "0", _culture);
        result.Should().BeOfType<float>().And.Be(2.5f);
    }

    [Fact]
    public void Instance_ShouldReturnSameInstance()
    {
        NullableNumericConverter.Instance.Should().BeSameAs(NullableNumericConverter.Instance);
    }
}
