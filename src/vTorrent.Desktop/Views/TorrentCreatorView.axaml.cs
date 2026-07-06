using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using vTorrent.Desktop.ViewModels.Tools;

namespace vTorrent.Desktop.Views;

public partial class TorrentCreatorView : UserControl
{
    public TorrentCreatorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is TorrentCreatorViewModel vm)
        {
            vm.BrowseFileRequested += OnBrowseFileRequested;
            vm.BrowseFolderRequested += OnBrowseFolderRequested;
            vm.SaveFileRequested += OnSaveFileRequested;
        }

        // Wire drag-and-drop on the drop zone
        var dropZone = this.FindControl<Border>("DropZone");
        if (dropZone != null)
        {
            dropZone.AddHandler(DragDrop.DropEvent, OnDrop);
            dropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        }
    }

    private async Task<string?> OnBrowseFileRequested()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select File",
            AllowMultiple = false,
        });

        return files.FirstOrDefault()?.Path.LocalPath;
    }

    private async Task<string?> OnBrowseFolderRequested()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Folder",
            AllowMultiple = false,
        });

        return folders.FirstOrDefault()?.Path.LocalPath;
    }

    private async Task<string?> OnSaveFileRequested(string suggestedName)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var result = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Torrent File",
            SuggestedFileName = suggestedName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Torrent Files") { Patterns = new[] { "*.torrent" } }
            }
        });

        return result?.Path.LocalPath;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not TorrentCreatorViewModel vm) return;

        var files = e.Data.GetFiles();
        if (files == null) return;

        var first = files.FirstOrDefault();
        if (first != null)
        {
            vm.SetContentPath(first.Path.LocalPath);
        }
    }
}
