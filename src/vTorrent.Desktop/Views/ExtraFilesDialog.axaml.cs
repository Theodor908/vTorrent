using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace vTorrent.Desktop.Views;

public partial class ExtraFilesDialog : Window
{
    private const int MaxVisibleFiles = 20;

    public ExtraFilesDialog()
    {
        InitializeComponent();

        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);

        this.Opened += (s, e) => FitHeightToContent();
    }

    public ExtraFilesDialog(string torrentName, IReadOnlyList<string> extraFiles) : this()
    {
        MessageText.Text = $"The folder for '{torrentName}' contains {extraFiles.Count} file(s) not part of the torrent:";

        var displayFiles = extraFiles.Take(MaxVisibleFiles).ToList();
        var lines = string.Join("\n", displayFiles);

        if (extraFiles.Count > MaxVisibleFiles)
        {
            lines += $"\n\n... and {extraFiles.Count - MaxVisibleFiles} more file(s)";
        }

        FileListText.Text = lines;
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

    private void OnDeleteAllClick(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnKeepClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    public static async Task<bool> ShowDialogAsync(Window owner, string torrentName, IReadOnlyList<string> extraFiles)
    {
        var dialog = new ExtraFilesDialog(torrentName, extraFiles);
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }
}
