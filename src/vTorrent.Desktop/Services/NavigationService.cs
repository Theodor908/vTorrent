using System;
using vTorrent.Abstractions.Interfaces.Services;

namespace vTorrent.Desktop.Services;

/// <summary>
/// Default implementation of INavigationService.
/// Manages navigation state and notifies subscribers of changes.
/// </summary>
public class NavigationService : INavigationService
{
    private NavigationSection _currentSection = NavigationSection.Overview;

    public NavigationSection CurrentSection => _currentSection;

    public event EventHandler<NavigationSection>? NavigationChanged;

    public void NavigateTo(NavigationSection section)
    {
        if (_currentSection == section) return;

        _currentSection = section;
        NavigationChanged?.Invoke(this, section);
    }
}
