using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using vTorrent.Desktop.ViewModels;

namespace vTorrent.Desktop.Views;

public partial class DeleteTorrentDialog : Window
{
    public bool DeleteFiles { get; private set; }
    public bool SecureWipe { get; private set; }
    public bool WipeMetadata { get; private set; }

    public DeleteTorrentDialog()
    {
        InitializeComponent();

        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);

        // Workaround: SizeToContent is broken with ExtendClientAreaToDecorationsHint
        // (https://github.com/AvaloniaUI/Avalonia/issues/4248)
        this.Opened += (s, e) => FitHeightToContent();
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

    /// <summary>
    /// Constructor for single torrent
    /// </summary>
    public DeleteTorrentDialog(string torrentName) : this()
    {
        MessageText.Text = $"Are you sure you want to remove '{torrentName}' from the transfer list?";
    }

    /// <summary>
    /// Constructor for multiple torrents
    /// </summary>
    public DeleteTorrentDialog(int torrentCount) : this()
    {
        MessageText.Text = $"Are you sure you want to remove {torrentCount} torrents from the transfer list?";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        DeleteFiles = DeleteFilesCheckBox.IsChecked == true;
        SecureWipe = SecureWipeCheckBox.IsChecked == true;
        WipeMetadata = WipeMetadataCheckBox.IsChecked == true;
        Close(true);
    }

    /// <summary>
    /// Show the delete confirmation dialog for a single torrent (legacy support)
    /// </summary>
    /// <param name="owner">Parent window</param>
    /// <param name="torrentName">Name of the torrent to delete</param>
    /// <returns>Tuple of (confirmed, deleteFiles) - confirmed is true if user clicked Delete</returns>
    public static async Task<(bool Confirmed, bool DeleteFiles, bool SecureWipe, bool WipeMetadata)> ShowDialogAsync(
        Window owner, string torrentName, bool defaultSecureWipe = false, bool defaultWipeMetadata = false)
    {
        var dialog = new DeleteTorrentDialog(torrentName);
        if (defaultSecureWipe || defaultWipeMetadata)
            dialog.DeleteFilesCheckBox.IsChecked = true;
        dialog.SecureWipeCheckBox.IsChecked = defaultSecureWipe;
        dialog.WipeMetadataCheckBox.IsChecked = defaultWipeMetadata;
        var result = await dialog.ShowDialog<bool?>(owner);
        return (result == true, dialog.DeleteFiles, dialog.SecureWipe, dialog.WipeMetadata);
    }

    /// <summary>
    /// Show the delete confirmation dialog for multiple torrents
    /// </summary>
    /// <param name="owner">Parent window</param>
    /// <param name="torrents">List of torrents to delete</param>
    /// <returns>Tuple of (confirmed, deleteFiles) - confirmed is true if user clicked Delete</returns>
    public static async Task<(bool Confirmed, bool DeleteFiles, bool SecureWipe, bool WipeMetadata)> ShowDialogAsync(
        Window owner, IReadOnlyList<TorrentViewModel> torrents, bool defaultSecureWipe = false, bool defaultWipeMetadata = false)
    {
        if (torrents.Count == 0)
            return (false, false, false, false);

        var dialog = torrents.Count == 1
            ? new DeleteTorrentDialog(torrents[0].Name)
            : new DeleteTorrentDialog(torrents.Count);

        if (defaultSecureWipe || defaultWipeMetadata)
            dialog.DeleteFilesCheckBox.IsChecked = true;
        dialog.SecureWipeCheckBox.IsChecked = defaultSecureWipe;
        dialog.WipeMetadataCheckBox.IsChecked = defaultWipeMetadata;
        var result = await dialog.ShowDialog<bool?>(owner);
        return (result == true, dialog.DeleteFiles, dialog.SecureWipe, dialog.WipeMetadata);
    }
}
