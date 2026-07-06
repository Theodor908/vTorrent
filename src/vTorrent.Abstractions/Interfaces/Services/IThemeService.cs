using System;

namespace vTorrent.Abstractions.Interfaces.Services;

/// <summary>
/// Theme mode options
/// </summary>
public enum ThemeMode
{
    /// <summary>Light theme with bright backgrounds</summary>
    Light,

    /// <summary>Dark theme with dark backgrounds</summary>
    Dark,

    /// <summary>Follow system/OS theme preference</summary>
    System
}

/// <summary>
/// Service for managing application theme switching.
/// Handles light/dark theme transitions and persistence.
/// </summary>
public interface IThemeService
{
    /// <summary>
    /// Gets the currently active theme mode
    /// </summary>
    ThemeMode CurrentTheme { get; }

    /// <summary>
    /// Gets whether the current theme is dark
    /// </summary>
    bool IsDarkTheme { get; }

    /// <summary>
    /// Event raised when the theme changes
    /// </summary>
    event EventHandler<ThemeMode>? ThemeChanged;

    /// <summary>
    /// Sets the application theme
    /// </summary>
    /// <param name="theme">The theme mode to apply</param>
    void SetTheme(ThemeMode theme);

    /// <summary>
    /// Toggles between light and dark themes
    /// </summary>
    void ToggleTheme();

    /// <summary>
    /// Applies the system/OS theme preference
    /// </summary>
    void ApplySystemTheme();

    /// <summary>
    /// Initializes the theme service and loads saved preference
    /// </summary>
    void Initialize();
}
