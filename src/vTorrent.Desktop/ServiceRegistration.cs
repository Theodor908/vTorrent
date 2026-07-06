using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Persistence;
using vTorrent.Desktop.ViewModels;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop;

/// <summary>
/// Extension methods for registering Desktop-specific services with the DI container.
/// Core and Storage registrations live in their own projects.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>
    /// Registers Desktop UI-layer services (TorrentManagerService, NotificationService, ThemeService, DialogService).
    /// </summary>
    public static IServiceCollection AddVTorrentDesktop(
        this IServiceCollection services,
        Avalonia.Application app)
    {
        services.AddSingleton<ITorrentManagerService, TorrentManagerService>();
        services.AddSingleton<INotificationService>(sp =>
            new NotificationService(sp.GetRequiredService<IOptionsMonitor<UISettings>>()));
        services.AddSingleton<IThemeService>(sp =>
        {
            var persistence = sp.GetRequiredService<SessionPersistence>();
            var ts = new ThemeService(app);
            ts.SetSettingsManager(persistence.SettingsManager!);
            return ts;
        });
        services.AddSingleton<ServerHostService>();
        services.AddSingleton<WebUIBundleScanner>();
        // ProfileManager is now registered in CoreServiceRegistration.AddVTorrentPersistence()
        services.AddSingleton<IDialogService>(sp => new DialogService(sp.GetRequiredService<ITorrentManagerService>(), sp));
        return services;
    }
}
