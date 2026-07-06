using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using vTorrent.Desktop.ViewModels.Tools;

namespace vTorrent.Desktop.Views;

public partial class TorrentEditorView : UserControl
{
    public TorrentEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is TorrentEditorViewModel vm)
        {
            vm.BrowseTorrentFileRequested += OnBrowseTorrentFileRequested;
            vm.SaveTorrentFileRequested += OnSaveTorrentFileRequested;
        }
    }

    private async Task<string?> OnBrowseTorrentFileRequested()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Torrent File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Torrent Files") { Patterns = new[] { "*.torrent" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } },
            }
        });

        return files.FirstOrDefault()?.Path.LocalPath;
    }

    private async Task<string?> OnSaveTorrentFileRequested(string suggestedName)
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
}
