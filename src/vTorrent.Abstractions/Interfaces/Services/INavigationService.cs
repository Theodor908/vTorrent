using System;

namespace vTorrent.Abstractions.Interfaces.Services;

/// <summary>
/// Interface for navigation between different views/sections.
/// Follows Interface Segregation Principle - only navigation concerns.
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// Currently active navigation section
    /// </summary>
    NavigationSection CurrentSection { get; }

    /// <summary>
    /// Navigate to a specific section
    /// </summary>
    void NavigateTo(NavigationSection section);

    /// <summary>
    /// Event raised when navigation changes
    /// </summary>
    event EventHandler<NavigationSection>? NavigationChanged;
}

/// <summary>
/// Available navigation sections in the application
/// </summary>
public enum NavigationSection
{
    Overview,
    Downloading,
    Seeding,
    Completed,
    Errored,
    Categories,
    Tags,
    Settings
}
