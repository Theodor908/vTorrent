using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace vTorrent.Desktop.Views;

public partial class TrayMenuPopup : Window
{
    public event Action? ShowMainWindowRequested;
    public event Action? AddTorrentRequested;
    public event Action? AddMagnetRequested;
    public event Action? SpeedLimitsRequested;
    public event Action? PauseResumeRequested;
    public event Action? QuitRequested;

    private bool _closing;

    public TrayMenuPopup()
    {
        InitializeComponent();
        Deactivated += (_, _) => ClosePopup();
    }

    public void SetPauseResumeText(bool isSessionPaused)
    {
        PauseResumeText.Text = isSessionPaused ? "Resume Session" : "Pause Session";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClosePopup();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void ClosePopup()
    {
        if (!_closing)
        {
            _closing = true;
            Close();
        }
    }

    private void OnShowClick(object? sender, RoutedEventArgs e)
    {
        ShowMainWindowRequested?.Invoke();
        ClosePopup();
    }

    private void OnAddTorrentClick(object? sender, RoutedEventArgs e)
    {
        AddTorrentRequested?.Invoke();
        ClosePopup();
    }

    private void OnAddMagnetClick(object? sender, RoutedEventArgs e)
    {
        AddMagnetRequested?.Invoke();
        ClosePopup();
    }

    private void OnSpeedLimitsClick(object? sender, RoutedEventArgs e)
    {
        SpeedLimitsRequested?.Invoke();
        ClosePopup();
    }

    private void OnPauseResumeClick(object? sender, RoutedEventArgs e)
    {
        PauseResumeRequested?.Invoke();
        ClosePopup();
    }

    private void OnQuitClick(object? sender, RoutedEventArgs e)
    {
        QuitRequested?.Invoke();
        ClosePopup();
    }
}
