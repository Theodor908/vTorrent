using System.Runtime.InteropServices;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Settings;

/// <summary>
/// Creates GlobalSettings with all correct defaults.
/// Used when global.json is missing or corrupted.
/// </summary>
public static class SettingsSeeder
{
    public static GlobalSettings CreateDefaults()
    {
        return new GlobalSettings
        {
            Connection = new ConnectionSettings(),
            Bandwidth = new BandwidthSettings(),
            Protocol = new ProtocolSettings(),
            Dht = new DhtSettings(),
            Disk = new DiskSettings
            {
                CloseFileInterval = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 240 : 0,
                NoAtimeStorage = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            },
            Queue = new QueueSettings(),
            Behavior = new BehaviorSettings(),
            Tracker = new TrackerSettings(),
            Peer = new PeerSettings(),
            Encryption = new EncryptionSettings(),
            AutoSave = new AutoSaveSettings(),
            Logging = new LoggingSettings(),
            UI = new UISettings(),
            WebSeed = new WebSeedSettings(),
            Privacy = new PrivacySettings(),
            Proxy = new ProxySettings(),
            Vpn = new VpnSettings(),
            Server = new ServerSettings()
        };
    }
}
