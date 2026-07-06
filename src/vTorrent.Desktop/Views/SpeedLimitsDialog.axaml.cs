using Avalonia.Controls;
using Avalonia.Interactivity;
using vTorrent.Desktop.ViewModels.Dialogs;

namespace vTorrent.Desktop.Views;

public partial class SpeedLimitsDialog : Window
{
    public SpeedLimitsDialog()
    {
        InitializeComponent();
        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);
    }

    private async void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SpeedLimitsDialogViewModel vm)
        {
            vm.DialogResult = true;
            await vm.ApplyAsync();
        }
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
