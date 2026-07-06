using System;
using Avalonia.Controls;
using Avalonia.Input;
using vTorrent.Desktop.ViewModels.Tools;

namespace vTorrent.Desktop.Views;

public partial class ToolsWindow : Window
{
    public ToolsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);

        var titleBar = this.FindControl<Border>("TitleBar");
        if (titleBar != null)
            titleBar.PointerPressed += OnTitleBarPointerPressed;
    }

    private ToolsWindowViewModel? _previousVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_previousVm != null)
            _previousVm.CloseRequested -= OnCloseRequested;

        if (DataContext is ToolsWindowViewModel vm)
        {
            vm.CloseRequested += OnCloseRequested;
            _previousVm = vm;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
