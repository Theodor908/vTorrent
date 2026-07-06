using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using vTorrent.Desktop.ViewModels.Dialogs;

namespace vTorrent.Desktop.Views;

public partial class AddTorrentDialog : Window
{
    private AddTorrentViewModel? _viewModel;

    public AddTorrentDialog()
    {
        InitializeComponent();

        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);

        // Enable window dragging from title bar
        var titleBar = this.FindControl<Border>("TitleBar");
        if (titleBar != null)
        {
            titleBar.PointerPressed += OnTitleBarPointerPressed;
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    public AddTorrentDialog(AddTorrentViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        // Subscribe to events
        viewModel.DialogAccepted += OnDialogAccepted;
        viewModel.DialogCancelled += OnDialogCancelled;
        viewModel.BrowseFolderRequested += OnBrowseFolderRequested;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Initialize the view model
        if (_viewModel != null)
        {
            _ = _viewModel.InitializeAsync();
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
    /// Show the dialog and load a torrent file
    /// </summary>
    public static async Task<bool> ShowDialogAsync(Window owner, AddTorrentViewModel viewModel, string torrentFilePath)
    {
        // Load the torrent file first
        await viewModel.LoadTorrentFileAsync(torrentFilePath);

        var dialog = new AddTorrentDialog(viewModel);
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }
}
