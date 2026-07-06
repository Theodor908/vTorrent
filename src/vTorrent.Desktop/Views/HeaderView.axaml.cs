using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace vTorrent.Desktop.Views;

public partial class HeaderView : UserControl
{
    public HeaderView()
    {
        InitializeComponent();

        // Wire up window control buttons
        MinimizeButton.Click += OnMinimizeClick;
        MaximizeButton.Click += OnMaximizeClick;
        CloseButton.Click += OnCloseClick;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var window = this.FindAncestorOfType<Window>();
            window?.BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        var window = this.FindAncestorOfType<Window>();
        if (window != null)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
    {
        var window = this.FindAncestorOfType<Window>();
        if (window != null)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        var window = this.FindAncestorOfType<Window>();
        window?.Close();
    }
}
