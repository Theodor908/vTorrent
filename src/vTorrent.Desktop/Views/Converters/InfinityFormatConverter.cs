using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace vTorrent.Desktop.Views;

/// <summary>
/// Converts a numeric value to display "∞" when the value is 0 (unlimited).
/// Used for speed limit inputs where 0 means unlimited.
/// </summary>
public class InfinityFormatConverter : IValueConverter
{
    public static readonly InfinityFormatConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal decimalValue)
        {
            return decimalValue == 0 ? "∞" : decimalValue.ToString(culture);
        }
        if (value is int intValue)
        {
            return intValue == 0 ? "∞" : intValue.ToString(culture);
        }
        if (value is double doubleValue)
        {
            return doubleValue == 0 ? "∞" : doubleValue.ToString(culture);
        }
        return value?.ToString() ?? "∞";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string strValue)
        {
            if (strValue == "∞" || string.IsNullOrWhiteSpace(strValue))
            {
                if (targetType == typeof(decimal) || targetType == typeof(decimal?))
                    return 0m;
                if (targetType == typeof(int) || targetType == typeof(int?))
                    return 0;
                if (targetType == typeof(double) || targetType == typeof(double?))
                    return 0.0;
            }

            if (decimal.TryParse(strValue, NumberStyles.Any, culture, out decimal result))
            {
                if (targetType == typeof(decimal) || targetType == typeof(decimal?))
                    return result;
                if (targetType == typeof(int) || targetType == typeof(int?))
                    return (int)result;
                if (targetType == typeof(double) || targetType == typeof(double?))
                    return (double)result;
            }
        }
        return 0;
    }
}
