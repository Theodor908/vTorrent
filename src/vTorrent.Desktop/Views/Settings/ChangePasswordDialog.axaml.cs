using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace vTorrent.Desktop.Views.Settings;

public partial class ChangePasswordDialog : Window
{
    public string? HashedPassword { get; private set; }

    public ChangePasswordDialog()
    {
        InitializeComponent();

        Helpers.WindowHelper.ApplyPlatformWindowStyle(this);

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

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var newPass = NewPasswordBox.Text;
        var confirm = ConfirmPasswordBox.Text;

        if (string.IsNullOrEmpty(newPass))
        {
            ErrorText.Text = "Password cannot be empty.";
            ErrorText.IsVisible = true;
            return;
        }

        if (newPass != confirm)
        {
            ErrorText.Text = "Passwords do not match.";
            ErrorText.IsVisible = true;
            return;
        }

        HashedPassword = BCrypt.Net.BCrypt.HashPassword(newPass, workFactor: 12);
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    /// <summary>
    /// Show the dialog and return the bcrypt-hashed password, or null if cancelled.
    /// </summary>
    public static async Task<string?> ShowDialogAsync(Window owner)
    {
        var dialog = new ChangePasswordDialog();
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true ? dialog.HashedPassword : null;
    }
}
