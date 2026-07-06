using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using vTorrent.Desktop.ViewModels.Dialogs;

namespace vTorrent.Desktop.Views;

public partial class AddMagnetLinkDialog : Window
{
    private AddMagnetLinkViewModel? _viewModel;

    public AddMagnetLinkDialog()
    {
        InitializeComponent();

        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    public AddMagnetLinkDialog(AddMagnetLinkViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        // Subscribe to events
        viewModel.DialogAccepted += OnDialogAccepted;
        viewModel.DialogCancelled += OnDialogCancelled;
        viewModel.BrowseFolderRequested += OnBrowseFolderRequested;
    }

    protected override async void OnOpened(EventArgs e)
    {
        try
        {
            base.OnOpened(e);

            // Try to paste from clipboard automatically if it looks like a magnet link
            await TryPasteFromClipboardAsync();

            // Focus the magnet URI text box
            var textBox = this.FindControl<TextBox>("MagnetUriTextBox");
            textBox?.Focus();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnOpened: {ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // Unsubscribe from events
        if (_viewModel != null)
        {
            _viewModel.DialogAccepted -= OnDialogAccepted;
            _viewModel.DialogCancelled -= OnDialogCancelled;
            _viewModel.BrowseFolderRequested -= OnBrowseFolderRequested;
        }
    }

    private async Task TryPasteFromClipboardAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;

            var text = await clipboard.GetTextAsync();
            if (!string.IsNullOrEmpty(text) && text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                // Auto-paste magnet link from clipboard
                if (_viewModel != null && string.IsNullOrEmpty(_viewModel.MagnetUri))
                {
                    _viewModel.MagnetUri = text;
                }
            }
        }
        catch
        {
            // Ignore clipboard errors
        }
    }

    private void OnDialogAccepted(object? sender, EventArgs e)
    {
        Close(true);
    }

    private void OnDialogCancelled(object? sender, EventArgs e)
    {
        Close(false);
    }

    private async void OnBrowseFolderRequested(object? sender, EventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Download Folder",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                var folder = folders[0];
                var path = folder.TryGetLocalPath();
                if (path != null)
                {
                    _viewModel?.SetSavePath(path);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error in OnBrowseFolderRequested: {ex.Message}");
        }
    }

    /// <summary>
    /// Show the dialog for adding a magnet link
    /// </summary>
    public static async Task<bool> ShowDialogAsync(Window owner, AddMagnetLinkViewModel viewModel)
    {
        var dialog = new AddMagnetLinkDialog(viewModel);
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }

    /// <summary>
    /// Show the dialog with a pre-filled magnet URI
    /// </summary>
    public static async Task<bool> ShowDialogAsync(Window owner, AddMagnetLinkViewModel viewModel, string magnetUri)
    {
        viewModel.MagnetUri = magnetUri;
        var dialog = new AddMagnetLinkDialog(viewModel);
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }
}
