using System;
using System.Collections.Generic;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Bench.Settings;

public sealed class SettingsRegistry
{
    private readonly List<SettingDefinition> _all = new();

    public IReadOnlyList<SettingDefinition> All => _all;

    public IReadOnlyList<SettingDefinition> GetGroup(string group)
    {
        var result = new List<SettingDefinition>();
        foreach (var d in _all)
            if (d.Group == group)
                result.Add(d);
        return result;
    }

    public IEnumerable<string> Groups()
    {
        var seen = new HashSet<string>();
        foreach (var d in _all)
            if (seen.Add(d.Group))
                yield return d.Group;
    }

    private void Add(string group, string key, string label, Type valueType,
        object min, object max, object step,
        Func<object> getter, Action<object> setter)
    {
        var def = new SettingDefinition
        {
            Group = group,
            Key = key,
            Label = label,
            ValueType = valueType,
            Min = min,
            Max = max,
            Step = step,
            Getter = getter,
            Setter = setter,
        };
        def.InitialValue = getter();
        _all.Add(def);
    }

    /// <summary>
    /// Build a registry wired to live MutableSettingsMonitor instances.
    /// The registry covers all 35 profile-relevant settings across
    /// Bandwidth, Connection, Queue, Behavior, Peer, and Disk settings.
    /// </summary>
    public static SettingsRegistry Build(
        MutableSettingsMonitor<BandwidthSettings> bandwidth,
        MutableSettingsMonitor<ConnectionSettings> connection,
        MutableSettingsMonitor<QueueSettings> queue,
        MutableSettingsMonitor<BehaviorSettings> behavior,
        MutableSettingsMonitor<PeerSettings> peer,
        MutableSettingsMonitor<DiskSettings> disk)
    {
        var reg = new SettingsRegistry();

        // === Bandwidth (5) ===
        reg.Add("Bandwidth", "globalDownloadLimit", "Global Download Limit",
            typeof(int), 0, 104_857_600, 102_400,
            () => (object)bandwidth.CurrentValue.GlobalDownloadLimit,
            v => bandwidth.Update(s => s.GlobalDownloadLimit = (int)v));

        reg.Add("Bandwidth", "globalUploadLimit", "Global Upload Limit",
            typeof(int), 0, 104_857_600, 102_400,
            () => (object)bandwidth.CurrentValue.GlobalUploadLimit,
            v => bandwidth.Update(s => s.GlobalUploadLimit = (int)v));

        reg.Add("Bandwidth", "perTorrentDownloadLimit", "Per-Torrent Download Limit",
            typeof(int), 0, 104_857_600, 102_400,
            () => (object)bandwidth.CurrentValue.PerTorrentDownloadLimit,
            v => bandwidth.Update(s => s.PerTorrentDownloadLimit = (int)v));

        reg.Add("Bandwidth", "perTorrentUploadLimit", "Per-Torrent Upload Limit",
            typeof(int), 0, 104_857_600, 102_400,
            () => (object)bandwidth.CurrentValue.PerTorrentUploadLimit,
            v => bandwidth.Update(s => s.PerTorrentUploadLimit = (int)v));

        reg.Add("Bandwidth", "mixedModeAlgorithm", "Mixed Mode Algorithm",
            typeof(MixedModeAlgorithm),
            MixedModeAlgorithm.PreferTcp, MixedModeAlgorithm.PreferTcp, MixedModeAlgorithm.PreferTcp,
            () => (object)bandwidth.CurrentValue.MixedModeAlgorithm,
            v => bandwidth.Update(s => s.MixedModeAlgorithm = (MixedModeAlgorithm)v));

        // === Connection (5) ===
        reg.Add("Connection", "maxGlobalConnections", "Max Global Connections",
            typeof(int), 1, 5000, 50,
            () => (object)connection.CurrentValue.MaxGlobalConnections,
            v => connection.Update(s => s.MaxGlobalConnections = (int)v));

        reg.Add("Connection", "maxConnectionsPerTorrent", "Max Connections Per Torrent",
            typeof(int), 1, 1000, 10,
            () => (object)connection.CurrentValue.MaxConnectionsPerTorrent,
            v => connection.Update(s => s.MaxConnectionsPerTorrent = (int)v));

        reg.Add("Connection", "maxUploadsPerTorrent", "Max Uploads Per Torrent",
            typeof(int), 1, 100, 1,
            () => (object)connection.CurrentValue.MaxUploadsPerTorrent,
            v => connection.Update(s => s.MaxUploadsPerTorrent = (int)v));

        reg.Add("Connection", "maxHalfOpenConnections", "Max Half-Open Connections",
            typeof(int), 1, 500, 10,
            () => (object)connection.CurrentValue.MaxHalfOpenConnections,
            v => connection.Update(s => s.MaxHalfOpenConnections = (int)v));

        reg.Add("Connection", "connectionSpeed", "Connection Speed",
            typeof(int), 1, 200, 5,
            () => (object)connection.CurrentValue.ConnectionSpeed,
            v => connection.Update(s => s.ConnectionSpeed = (int)v));

        // === Queue (5) ===
        reg.Add("Queue", "maxActiveDownloads", "Max Active Downloads",
            typeof(int), 1, 50, 1,
            () => (object)queue.CurrentValue.MaxActiveDownloads,
            v => queue.Update(s => s.MaxActiveDownloads = (int)v));

        reg.Add("Queue", "maxActiveSeeds", "Max Active Seeds",
            typeof(int), -1, 50, 1,
            () => (object)queue.CurrentValue.MaxActiveSeeds,
            v => queue.Update(s => s.MaxActiveSeeds = (int)v));

        reg.Add("Queue", "maxActiveTorrents", "Max Active Torrents",
            typeof(int), 1, 100, 1,
            () => (object)queue.CurrentValue.MaxActiveTorrents,
            v => queue.Update(s => s.MaxActiveTorrents = (int)v));

        reg.Add("Queue", "dontCountSlowTorrents", "Don't Count Slow Torrents",
            typeof(bool), false, true, false,
            () => (object)queue.CurrentValue.DontCountSlowTorrents,
            v => queue.Update(s => s.DontCountSlowTorrents = (bool)v));

        reg.Add("Queue", "connectSeedEveryNDownload", "Connect Seed Every N Download",
            typeof(int), 1, 100, 1,
            () => (object)queue.CurrentValue.ConnectSeedEveryNDownload,
            v => queue.Update(s => s.ConnectSeedEveryNDownload = (int)v));

        // === Choking (6) ===
        reg.Add("Choking", "chokingAlgorithm", "Choking Algorithm",
            typeof(ChokingAlgorithm),
            ChokingAlgorithm.FixedSlots, ChokingAlgorithm.FixedSlots, ChokingAlgorithm.FixedSlots,
            () => (object)behavior.CurrentValue.ChokingAlgorithm,
            v => behavior.Update(s => s.ChokingAlgorithm = (ChokingAlgorithm)v));

        reg.Add("Choking", "seedChokingAlgorithm", "Seed Choking Algorithm",
            typeof(SeedChokingAlgorithm),
            SeedChokingAlgorithm.FastestUpload, SeedChokingAlgorithm.FastestUpload, SeedChokingAlgorithm.FastestUpload,
            () => (object)behavior.CurrentValue.SeedChokingAlgorithm,
            v => behavior.Update(s => s.SeedChokingAlgorithm = (SeedChokingAlgorithm)v));

        reg.Add("Choking", "unchokeSlots", "Unchoke Slots",
            typeof(int), 1, 64, 1,
            () => (object)behavior.CurrentValue.UnchokeSlots,
            v => behavior.Update(s => s.UnchokeSlots = (int)v));

        reg.Add("Choking", "unchokeInterval", "Unchoke Interval (s)",
            typeof(int), 1, 120, 1,
            () => (object)peer.CurrentValue.UnchokeInterval,
            v => peer.Update(s => s.UnchokeInterval = (int)v));

        reg.Add("Choking", "optimisticUnchokeInterval", "Optimistic Unchoke Interval (s)",
            typeof(int), 1, 300, 5,
            () => (object)peer.CurrentValue.OptimisticUnchokeInterval,
            v => peer.Update(s => s.OptimisticUnchokeInterval = (int)v));

        reg.Add("Choking", "numOptimisticUnchokeSlots", "Optimistic Unchoke Slots",
            typeof(int), 0, 10, 1,
            () => (object)peer.CurrentValue.NumOptimisticUnchokeSlots,
            v => peer.Update(s => s.NumOptimisticUnchokeSlots = (int)v));

        // === Peer (4) ===
        reg.Add("Peer", "peerTurnover", "Peer Turnover (%)",
            typeof(int), 0, 100, 1,
            () => (object)behavior.CurrentValue.PeerTurnover,
            v => behavior.Update(s => s.PeerTurnover = (int)v));

        reg.Add("Peer", "peerTurnoverCutoff", "Peer Turnover Cutoff (%)",
            typeof(int), 0, 100, 5,
            () => (object)behavior.CurrentValue.PeerTurnoverCutoff,
            v => behavior.Update(s => s.PeerTurnoverCutoff = (int)v));

        reg.Add("Peer", "peerTurnoverInterval", "Peer Turnover Interval (s)",
            typeof(int), 10, 3600, 30,
            () => (object)behavior.CurrentValue.PeerTurnoverInterval,
            v => behavior.Update(s => s.PeerTurnoverInterval = (int)v));

        reg.Add("Peer", "maxPendingBlocksPerPeer", "Max Pending Blocks Per Peer",
            typeof(int), 1, 2000, 50,
            () => (object)peer.CurrentValue.MaxPendingBlocksPerPeer,
            v => peer.Update(s => s.MaxPendingBlocksPerPeer = (int)v));

        // === Disk (4) ===
        reg.Add("Disk", "backendType", "Disk Backend",
            typeof(DiskBackendType),
            DiskBackendType.Auto, DiskBackendType.Auto, DiskBackendType.Auto,
            () => (object)disk.CurrentValue.BackendType,
            v => disk.Update(s => s.BackendType = (DiskBackendType)v));

        reg.Add("Disk", "cacheSize", "Cache Size (bytes)",
            typeof(long), 0L, 1_073_741_824L, 16_777_216L,
            () => (object)disk.CurrentValue.CacheSize,
            v => disk.Update(s => s.CacheSize = (long)v));

        reg.Add("Disk", "maxOutstandingDiskRequests", "Max Outstanding Disk Requests",
            typeof(int), 1, 512, 16,
            () => (object)disk.CurrentValue.MaxOutstandingDiskRequests,
            v => disk.Update(s => s.MaxOutstandingDiskRequests = (int)v));

        reg.Add("Disk", "hashThreads", "Hash Threads",
            typeof(int), 1, 16, 1,
            () => (object)disk.CurrentValue.HashThreads,
            v => disk.Update(s => s.HashThreads = (int)v));

        // === Seeding (4) ===
        reg.Add("Seeding", "seedRatioLimit", "Seed Ratio Limit",
            typeof(float), 0f, 100f, 0.1f,
            () => (object)behavior.CurrentValue.SeedRatioLimit,
            v => behavior.Update(s => s.SeedRatioLimit = (float)v));

        reg.Add("Seeding", "seedTimeLimit", "Seed Time Limit (min)",
            typeof(int), 0, 10080, 60,
            () => (object)behavior.CurrentValue.SeedTimeLimit,
            v => behavior.Update(s => s.SeedTimeLimit = (int)v));

        reg.Add("Seeding", "pauseOnSeedComplete", "Pause On Seed Complete",
            typeof(bool), false, true, false,
            () => (object)behavior.CurrentValue.PauseOnSeedComplete,
            v => behavior.Update(s => s.PauseOnSeedComplete = (bool)v));

        reg.Add("Seeding", "removeOnSeedComplete", "Remove On Seed Complete",
            typeof(bool), false, true, false,
            () => (object)behavior.CurrentValue.RemoveOnSeedComplete,
            v => behavior.Update(s => s.RemoveOnSeedComplete = (bool)v));

        // === Picker (2) ===
        reg.Add("Picker", "initialPickerThreshold", "Initial Picker Threshold",
            typeof(int), 0, 100, 1,
            () => (object)behavior.CurrentValue.InitialPickerThreshold,
            v => behavior.Update(s => s.InitialPickerThreshold = (int)v));

        reg.Add("Picker", "wholePiecesThreshold", "Whole Pieces Threshold (s)",
            typeof(int), 0, 300, 5,
            () => (object)behavior.CurrentValue.WholePiecesThreshold,
            v => behavior.Update(s => s.WholePiecesThreshold = (int)v));

        return reg;
    }
}
