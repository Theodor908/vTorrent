using System;
using System.Linq;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Settings;

namespace vTorrent.Desktop.Services;

/// <summary>
/// Handles runtime theme switching by swapping Avalonia resource dictionaries.
/// </summary>
public class ThemeService : IThemeService
{
    private readonly Application _app;
    private SettingsManager? _settingsManager;
    private ThemeMode _currentTheme = ThemeMode.Dark;

    private const string DarkThemeUri = "avares://vTorrent/Assets/Themes/DarkTheme.axaml";
    private const string LightThemeUri = "avares://vTorrent/Assets/Themes/LightTheme.axaml";

    public ThemeMode CurrentTheme => _currentTheme;

    public bool IsDarkTheme => _currentTheme == ThemeMode.Dark;

    public event EventHandler<ThemeMode>? ThemeChanged;

    public ThemeService(Application app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
    }

    /// <summary>
    /// Set the settings manager for theme persistence
    /// </summary>
    public void SetSettingsManager(SettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
    }

    /// <summary>
    /// Initializes the theme service and applies saved or default theme
    /// </summary>
    public void Initialize()
    {
        // Try to load saved preference
        var savedTheme = LoadSavedTheme();

        if (savedTheme == ThemeMode.System)
        {
            ApplySystemTheme();
        }
        else
        {
            ApplyThemeInternal(savedTheme);
            _currentTheme = savedTheme;
        }
    }

    public void SetTheme(ThemeMode theme)
    {
        if (theme == ThemeMode.System)
        {
            ApplySystemTheme();
            SaveThemePreference(ThemeMode.System);
            return;
        }

        if (_currentTheme == theme) return;

        ApplyThemeInternal(theme);
        _currentTheme = theme;
        SaveThemePreference(theme);
        ThemeChanged?.Invoke(this, theme);
    }

    public void ToggleTheme()
    {
        var newTheme = _currentTheme == ThemeMode.Dark
            ? ThemeMode.Light
            : ThemeMode.Dark;

        SetTheme(newTheme);
    }

    public void ApplySystemTheme()
    {
        var platformSettings = _app.PlatformSettings;
        if (platformSettings != null)
        {
            var colorValues = platformSettings.GetColorValues();
            // PlatformThemeVariant is Dark when system is in dark mode
            var systemTheme = colorValues.ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark
                ? ThemeMode.Dark
                : ThemeMode.Light;

            ApplyThemeInternal(systemTheme);
            _currentTheme = systemTheme;
            ThemeChanged?.Invoke(this, systemTheme);
        }
        else
        {
            // Fallback to dark theme if platform settings unavailable
            ApplyThemeInternal(ThemeMode.Dark);
            _currentTheme = ThemeMode.Dark;
        }
    }

    private void ApplyThemeInternal(ThemeMode theme)
    {
        var themeUri = theme == ThemeMode.Dark
            ? new Uri(DarkThemeUri)
            : new Uri(LightThemeUri);

        try
        {
            var resources = _app.Resources.MergedDictionaries;

            // Find and remove existing theme dictionary
            var existingTheme = resources
                .OfType<ResourceInclude>()
                .FirstOrDefault(ri =>
                    ri.Source?.ToString().Contains("Theme.axaml") == true);

            if (existingTheme != null)
            {
                resources.Remove(existingTheme);
            }

            // Add the new theme dictionary
            var newTheme = new ResourceInclude(themeUri) { Source = themeUri };
            resources.Add(newTheme);

            // Update RequestedThemeVariant to change native title bar color
            _app.RequestedThemeVariant = theme == ThemeMode.Dark
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to apply theme: {ex.Message}");
        }
    }

    private ThemeMode LoadSavedTheme()
    {
        if (_settingsManager == null)
            return ThemeMode.Dark;

        var themeString = _settingsManager.Current.UI.Theme;
        return themeString switch
        {
            "Light" => ThemeMode.Light,
            "System" => ThemeMode.System,
            _ => ThemeMode.Dark
        };
    }

    private void SaveThemePreference(ThemeMode theme)
    {
        if (_settingsManager == null)
            return;

        var themeString = theme switch
        {
            ThemeMode.Light => "Light",
            ThemeMode.System => "System",
            _ => "Dark"
        };

        _settingsManager.Current.UI.Theme = themeString;
        _ = _settingsManager.SaveAsync();
    }
}
