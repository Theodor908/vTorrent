using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Interfaces;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Interfaces.Storage;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Engine;
using vTorrent.Core.IO;
using vTorrent.Core.Orchestration;
using vTorrent.Core.Persistence;
using vTorrent.Core.PeerCommunication.Transport;
using vTorrent.Core.PieceIO;
using vTorrent.Core.Services;
using vTorrent.Core.Network;
using vTorrent.Core.Settings;

namespace vTorrent.Core.Registration;

/// <summary>
/// Extension methods for registering session-scoped Core services with the DI container.
/// Per-torrent services (IPeerRegistry, IStatisticsTracker, PeerSelector, etc.) are created
/// by EngineFactory per-torrent and are NOT registered here.
/// </summary>
public static class CoreServiceRegistration
{
    public static IServiceCollection AddVTorrentCore(this IServiceCollection services, ILoggerFactory loggerFactory)
    {
        services.AddSingleton(loggerFactory);
        services.AddSingleton<ResourceAllocator>();
        services.AddSingleton<AlertManager>();
        services.AddSingleton<NetworkChangeNotifier>();
        services.AddSingleton<EngineFactory>();
        services.AddSingleton<ISecureFileWiper, SecureFileWiper>();
        services.AddSingleton<DeletionWorker>();
        services.AddSingleton<TorrentOrchestrator>();
        services.AddSingleton(sp => new UdpSocketManager(
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<UdpSocketManager>()));
        services.AddSingleton<IVpnStatusService>(sp => sp.GetRequiredService<TorrentOrchestrator>());
        services.AddSingleton<ITorrentService, TorrentService>();

        // Settings monitors for live change notification (IOptionsMonitor<T> pattern)
        services.AddSingleton<SettingsMonitor<BehaviorSettings>>();
        services.AddSingleton<IOptionsMonitor<BehaviorSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<BehaviorSettings>>());

        services.AddSingleton<SettingsMonitor<BandwidthSettings>>();
        services.AddSingleton<IOptionsMonitor<BandwidthSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<BandwidthSettings>>());

        services.AddSingleton<SettingsMonitor<QueueSettings>>();
        services.AddSingleton<IOptionsMonitor<QueueSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<QueueSettings>>());

        services.AddSingleton<SettingsMonitor<PeerSettings>>();
        services.AddSingleton<IOptionsMonitor<PeerSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<PeerSettings>>());

        services.AddSingleton<SettingsMonitor<DiskSettings>>();
        services.AddSingleton<IOptionsMonitor<DiskSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<DiskSettings>>());

        // --- Tier 1: Core engine settings ---
        services.AddSingleton<SettingsMonitor<ConnectionSettings>>();
        services.AddSingleton<IOptionsMonitor<ConnectionSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<ConnectionSettings>>());

        services.AddSingleton<SettingsMonitor<TrackerSettings>>();
        services.AddSingleton<IOptionsMonitor<TrackerSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<TrackerSettings>>());

        services.AddSingleton<SettingsMonitor<DhtSettings>>();
        services.AddSingleton<IOptionsMonitor<DhtSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<DhtSettings>>());

        services.AddSingleton<SettingsMonitor<EncryptionSettings>>();
        services.AddSingleton<IOptionsMonitor<EncryptionSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<EncryptionSettings>>());

        // --- Tier 2: Infrastructure settings ---
        services.AddSingleton<SettingsMonitor<WebSeedSettings>>();
        services.AddSingleton<IOptionsMonitor<WebSeedSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<WebSeedSettings>>());

        services.AddSingleton<SettingsMonitor<ProxySettings>>();
        services.AddSingleton<IOptionsMonitor<ProxySettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<ProxySettings>>());

        services.AddSingleton<SettingsMonitor<VpnSettings>>();
        services.AddSingleton<IOptionsMonitor<VpnSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<VpnSettings>>());

        services.AddSingleton<SettingsMonitor<AutoSaveSettings>>();
        services.AddSingleton<IOptionsMonitor<AutoSaveSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<AutoSaveSettings>>());

        // --- Tier 3: Peripheral settings ---
        services.AddSingleton<SettingsMonitor<LoggingSettings>>();
        services.AddSingleton<IOptionsMonitor<LoggingSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<LoggingSettings>>());

        services.AddSingleton<SettingsMonitor<PrivacySettings>>();
        services.AddSingleton<IOptionsMonitor<PrivacySettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<PrivacySettings>>());

        services.AddSingleton<SettingsMonitor<ProtocolSettings>>();
        services.AddSingleton<IOptionsMonitor<ProtocolSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<ProtocolSettings>>());

        services.AddSingleton<SettingsMonitor<UISettings>>();
        services.AddSingleton<IOptionsMonitor<UISettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<UISettings>>());

        // --- I2P settings ---
        services.AddSingleton<SettingsMonitor<I2pSettings>>();
        services.AddSingleton<IOptionsMonitor<I2pSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<I2pSettings>>());

        // --- Peer class settings ---
        services.AddSingleton<SettingsMonitor<PeerClassSettings>>();
        services.AddSingleton<IOptionsMonitor<PeerClassSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<PeerClassSettings>>());

        // --- Server settings ---
        services.AddSingleton<SettingsMonitor<ServerSettings>>();
        services.AddSingleton<IOptionsMonitor<ServerSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<ServerSettings>>());

        // --- Schedule settings ---
        services.AddSingleton<SettingsMonitor<ScheduleSettings>>();
        services.AddSingleton<IOptionsMonitor<ScheduleSettings>>(sp =>
            sp.GetRequiredService<SettingsMonitor<ScheduleSettings>>());

        return services;
    }

    public static IServiceCollection AddVTorrentPersistence(
        this IServiceCollection services,
        string dataDirectory)
    {
        services.AddSingleton(sp =>
            new SessionPersistence(
                dataDirectory,
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ISecureFileWiper>(),
                sp.GetRequiredService<DeletionWorker>()));

        // ProfileManager needs the data directory for profile JSON storage
        services.AddSingleton(new ProfileManager(dataDirectory));

        return services;
    }
}
