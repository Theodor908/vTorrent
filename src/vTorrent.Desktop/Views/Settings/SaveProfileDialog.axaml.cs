using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using vTorrent.Desktop.ViewModels.Settings;

namespace vTorrent.Desktop.Views.Settings;

public partial class SaveProfileDialog : Window
{
    public SaveProfileDialog()
    {
        InitializeComponent();
        DataContext = new SaveProfileDialogViewModel();

        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);

        this.Opened += (s, e) =>
        {
            FitHeightToContent();
            if (DataContext is SaveProfileDialogViewModel vm)
                UpdateColorSwatchHighlight(vm.SelectedColor);
        };
    }

    private void FitHeightToContent()
    {
        if (Content is Control root)
        {
            root.Measure(new Avalonia.Size(Width, double.PositiveInfinity));
            var desiredHeight = root.DesiredSize.Height;
            if (desiredHeight > 0)
            {
                Height = desiredHeight;
            }
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnColorSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string color && DataContext is SaveProfileDialogViewModel vm)
        {
            vm.SelectedColor = color;
            UpdateColorSwatchHighlight(color);
        }
    }

    private void UpdateColorSwatchHighlight(string selectedColor)
    {
        var wrapPanel = this.FindControl<WrapPanel>("ColorSwatches");
        if (wrapPanel == null) return;

        foreach (var child in wrapPanel.Children)
        {
            if (child is Button swatch)
            {
                var isSelected = swatch.Tag is string tag &&
                                 string.Equals(tag, selectedColor, System.StringComparison.OrdinalIgnoreCase);
                swatch.BorderBrush = isSelected
                    ? Avalonia.Media.Brushes.White
                    : Avalonia.Media.Brushes.Transparent;
            }
        }
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SaveProfileDialogViewModel vm)
        {
            vm.CreateCommand.Execute(null);
            if (vm.IsConfirmed)
            {
                Close(true);
            }
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SaveProfileDialogViewModel vm)
        {
            vm.CancelCommand.Execute(null);
        }
        Close(false);
    }
}
