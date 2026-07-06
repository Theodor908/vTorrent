using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels.Tools;

public partial class ToolsWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private int _selectedTabIndex;

    public TorrentCreatorViewModel Creator { get; }
    public TorrentEditorViewModel Editor { get; }

    public event EventHandler? CloseRequested;

    /// <summary>
    /// Optional info hash to pre-select in the editor when opened via "Edit Torrent" context menu.
    /// </summary>
    public string? PreselectedInfoHash { get; set; }

    public ToolsWindowViewModel(ITorrentManagerService? torrentManager = null)
    {
        Creator = new TorrentCreatorViewModel(torrentManager);
        Editor = new TorrentEditorViewModel(torrentManager);
    }

    public void Initialize()
    {
        Editor.Initialize(PreselectedInfoHash);
    }

    [RelayCommand]
    private void SelectTab(string index)
    {
        if (int.TryParse(index, out var i))
            SelectedTabIndex = i;
    }

    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
