using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace vTorrent.Desktop.Views;

/// <summary>
/// Converts an integer value to a boolean indicating whether it equals the parameter.
/// Used for tab selection in settings window.
/// </summary>
public class IntEqualConverter : IValueConverter
{
    public static readonly IntEqualConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue && parameter != null)
        {
            if (int.TryParse(parameter.ToString(), out int compareValue))
            {
                return intValue == compareValue;
            }
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
