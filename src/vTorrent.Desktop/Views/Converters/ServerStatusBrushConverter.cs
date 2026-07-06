using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.Views.Converters;

public class ServerStatusBrushConverter : IValueConverter
{
    public static readonly ServerStatusBrushConverter Instance = new();

    private static readonly SolidColorBrush Green = new(Color.Parse("#4CAF50"));
    private static readonly SolidColorBrush Amber = new(Color.Parse("#FFB74D"));
    private static readonly SolidColorBrush Red = new(Color.Parse("#EF5350"));
    private static readonly SolidColorBrush Gray = new(Color.Parse("#666666"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is ServerStatus status ? status switch
        {
            ServerStatus.Running => Green,
            ServerStatus.Starting or ServerStatus.Restarting => Amber,
            ServerStatus.Error => Red,
            _ => Gray
        } : Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
