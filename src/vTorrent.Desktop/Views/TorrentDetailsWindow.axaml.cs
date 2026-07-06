using System;
using Avalonia.Controls;
using Avalonia.Input;
using vTorrent.Desktop.ViewModels;

namespace vTorrent.Desktop.Views;

public partial class TorrentDetailsWindow : Window
{
    public TorrentDetailsWindow()
    {
        InitializeComponent();

        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);

        // Title bar dragging
        var titleBar = this.FindControl<Border>("TitleBar");
        if (titleBar != null)
        {
            titleBar.PointerPressed += OnTitleBarPointerPressed;
        }

        // Close button
        var closeButton = this.FindControl<Button>("CloseButton");
        if (closeButton != null)
        {
            closeButton.Click += (_, _) => Close();
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

}
