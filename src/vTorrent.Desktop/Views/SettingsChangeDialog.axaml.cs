using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Desktop.Views;

public partial class SettingsChangeDialog : Window
{
    public SettingsPropagationMode Result { get; private set; } = SettingsPropagationMode.None;

    public SettingsChangeDialog()
    {
        InitializeComponent();
        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);
        this.Opened += (s, e) => FitHeightToContent();
    }

    public SettingsChangeDialog(string settingDisplayName, string oldValue, string newValue) : this()
    {
        MessageText.Text = $"You changed '{settingDisplayName}' from {oldValue} to {newValue}.";
        DescriptionText.Text = "Some existing torrents may have per-torrent overrides. How should this change apply?";
    }

    private void FitHeightToContent()
    {
        if (Content is Control root)
        {
            root.Measure(new Avalonia.Size(Width, double.PositiveInfinity));
            var desiredHeight = root.DesiredSize.Height;
            if (desiredHeight > 0) Height = desiredHeight;
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnApplyAllClick(object? sender, RoutedEventArgs e)
    {
        Result = SettingsPropagationMode.OverrideAll;
        Close(true);
    }

    private void OnApplyDefaultsOnlyClick(object? sender, RoutedEventArgs e)
    {
        Result = SettingsPropagationMode.OnlyMatchingOldDefault;
        Close(true);
    }

    private void OnSkipClick(object? sender, RoutedEventArgs e)
    {
        Result = SettingsPropagationMode.None;
        Close(false);
    }

    /// <summary>
    /// Show the settings change propagation dialog.
    /// </summary>
    public static async Task<SettingsPropagationMode> ShowDialogAsync(
        Window owner, string settingDisplayName, string oldValue, string newValue)
    {
        var dialog = new SettingsChangeDialog(settingDisplayName, oldValue, newValue);
        await dialog.ShowDialog<bool?>(owner);
        return dialog.Result;
    }
}
