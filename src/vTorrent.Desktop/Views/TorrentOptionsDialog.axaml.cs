using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using vTorrent.Desktop.ViewModels;
using vTorrent.Desktop.ViewModels.Dialogs;

namespace vTorrent.Desktop.Views;

public partial class TorrentOptionsDialog : Window
{
    private TorrentOptionsViewModel? _viewModel;

    public TorrentOptionsDialog()
    {
        InitializeComponent();

        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);

        // Workaround: SizeToContent is broken with ExtendClientAreaToDecorationsHint
        // (https://github.com/AvaloniaUI/Avalonia/issues/4248)
        // Measure content and set Height programmatically after layout
        this.Opened += (s, e) => FitHeightToContent();

        // Enable window dragging from title bar
        var titleBar = this.FindControl<Border>("TitleBar");
        if (titleBar != null)
        {
            titleBar.PointerPressed += OnTitleBarPointerPressed;
        }
    }

    private void FitHeightToContent()
    {
        if (Content is Control root)
        {
            root.Measure(new Avalonia.Size(Width, double.PositiveInfinity));
            var desiredHeight = root.DesiredSize.Height;
            if (desiredHeight > 0)
            {
                Height = Math.Min(desiredHeight, 700);
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

    public TorrentOptionsDialog(TorrentOptionsViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        // Subscribe to events
        viewModel.DialogAccepted += OnDialogAccepted;
        viewModel.DialogCancelled += OnDialogCancelled;
        viewModel.BrowseFolderRequested += OnBrowseFolderRequested;
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
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Save Location",
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

    /// <summary>
    /// Show the dialog for a specific torrent
    /// </summary>
    public static async Task<bool> ShowDialogAsync(Window owner, TorrentOptionsViewModel viewModel, TorrentViewModel torrent)
    {
        // Initialize the view model with the torrent
        await viewModel.InitializeAsync(torrent);

        var dialog = new TorrentOptionsDialog(viewModel);
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }

    /// <summary>
    /// Show the dialog for multiple torrents
    /// </summary>
    public static async Task<bool> ShowDialogAsync(Window owner, TorrentOptionsViewModel viewModel, IReadOnlyList<TorrentViewModel> torrents)
    {
        // Initialize the view model with all torrents
        await viewModel.InitializeAsync(torrents);

        var dialog = new TorrentOptionsDialog(viewModel);
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }
}
