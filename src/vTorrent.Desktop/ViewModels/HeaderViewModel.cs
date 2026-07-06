using CommunityToolkit.Mvvm.ComponentModel;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels;

/// <summary>
/// ViewModel for the application header/logo bar.
/// </summary>
public partial class HeaderViewModel : BaseViewModel
{
    private readonly IThemeService? _themeService;

    [ObservableProperty]
    private bool _isDarkTheme = true;

    /// <summary>
    /// Gets the appropriate logo path based on current theme.
    /// Dark logo for dark theme, light logo for light theme.
    /// </summary>
    public string LogoPath => IsDarkTheme
        ? "/Assets/Images/dark_logo.svg"
        : "/Assets/Images/light_logo.svg";

    /// <summary>
    /// Design-time constructor
    /// </summary>
    public HeaderViewModel() : this(null)
    {
    }

    /// <summary>
    /// Runtime constructor with theme service injection
    /// </summary>
    public HeaderViewModel(IThemeService? themeService)
    {
        _themeService = themeService;

        if (_themeService != null)
        {
            _isDarkTheme = _themeService.IsDarkTheme;
            _themeService.ThemeChanged += OnThemeChanged;
        }
    }

    private void OnThemeChanged(object? sender, ThemeMode theme)
    {
        IsDarkTheme = theme == ThemeMode.Dark;
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        OnPropertyChanged(nameof(LogoPath));
    }
}
