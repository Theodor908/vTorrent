using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace vTorrent.Desktop.Views;

public class FileTreeIconConverter : IValueConverter
{
    public static readonly FileTreeIconConverter Instance = new();

    // Phosphor icons: Folder = \uE24A, File = \uE230
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "\uE24A" : "\uE230";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
