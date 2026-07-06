using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace vTorrent.Desktop.Views.Converters;

/// <summary>
/// Two-way converter between ViewModel numeric types (int, float) and
/// Avalonia NumericUpDown's decimal? Value property.
/// When the user clears the field (null), returns the fallback from ConverterParameter.
/// </summary>
public sealed class NullableNumericConverter : IValueConverter
{
    public static readonly NullableNumericConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            int i => (decimal)i,
            float f => (decimal)f,
            double d => (decimal)d,
            long l => (decimal)l,
            _ => 0m
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var decimalValue = value as decimal?;

        if (decimalValue == null)
        {
            return ParseFallback(parameter, targetType);
        }

        return ConvertToTarget(decimalValue.Value, targetType);
    }

    private static object ParseFallback(object? parameter, Type targetType)
    {
        if (parameter is string s && !string.IsNullOrEmpty(s))
        {
            return ConvertToTarget(decimal.Parse(s, CultureInfo.InvariantCulture), targetType);
        }

        return ConvertToTarget(0m, targetType);
    }

    private static object ConvertToTarget(decimal value, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(int)) return (int)value;
        if (underlying == typeof(float)) return (float)value;
        if (underlying == typeof(double)) return (double)value;
        if (underlying == typeof(long)) return (long)value;
        return (int)value;
    }
}
