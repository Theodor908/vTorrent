using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using vTorrent.Desktop.ViewModels;

namespace vTorrent.Desktop.Views;

public partial class Sidebar : UserControl
{
    public Sidebar()
    {
        InitializeComponent();
        // DataContext is inherited from parent or set explicitly by MainWindow
    }

    private void OnCategoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Button button && button.Tag is SidebarMenuItemViewModel item)
        {
            // Don't allow editing the "All" category
            if (item.DatabaseId == null) return;

            if (DataContext is SidebarViewModel viewModel)
            {
                viewModel.EditCategoryCommand.Execute(item);
            }
        }
        e.Handled = true;
    }

    private void OnTagDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Button button && button.Tag is SidebarMenuItemViewModel item)
        {
            if (item.DatabaseId == null) return;

            if (DataContext is SidebarViewModel viewModel)
            {
                viewModel.EditTagCommand.Execute(item);
            }
        }
        e.Handled = true;
    }

    private void OnCategoryEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is SidebarMenuItemViewModel item)
        {
            if (item.DatabaseId == null) return;

            if (DataContext is SidebarViewModel viewModel)
            {
                viewModel.EditCategoryCommand.Execute(item);
            }
        }
        e.Handled = true;
    }

    private void OnTagEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is SidebarMenuItemViewModel item)
        {
            if (item.DatabaseId == null) return;

            if (DataContext is SidebarViewModel viewModel)
            {
                viewModel.EditTagCommand.Execute(item);
            }
        }
        e.Handled = true;
    }
}
