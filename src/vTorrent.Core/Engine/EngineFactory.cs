using System;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using vTorrent.Core.Orchestration.Alerts;

using vTorrent.Core.Orchestration.Bandwidth;

using vTorrent.Storage;

using vTorrent.Core.PeerCommunication.Bandwidth;

using vTorrent.Core.PeerCommunication.Transport;

using vTorrent.Abstractions.Interfaces;

using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Settings;

using vTorrent.Abstractions.Interfaces.Engine;

using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Core.Orchestration;
using vTorrent.Core.Download;
using vTorrent.Core.Upload;
using vTorrent.Core.PeerCommunication;
using vTorrent.Core.Network.I2P;
using vTorrent.Core.PeerCommunication.Transport;

namespace vTorrent.Core.Engine;

/// <summary>

/// Factory for creating TorrentEngine instances with proper dependency injection.

/// Wires events to the alert manager for centralized event handling.

/// </summary>

public class EngineFactory

{

    private readonly ILoggerFactory _loggerFactory;

    private readonly AlertManager _alertManager;

    private readonly ResourceAllocator _resourceAllocator;

    private readonly GlobalBandwidthCoordinator? _bandwidthCoordinator;

    private readonly PeerSettings _defaultPeerSettings;

    private readonly IOptionsMonitor<TrackerSettings> _trackerMonitor;

    private readonly ITorrentDialog? _torrentDialog;

    private readonly TorrentDatabase? _database;

    private readonly ITransportConnector? _transportConnector;

    private readonly IExternalIpVoter? _externalIpVoter;

    private readonly IOptionsMonitor<BehaviorSettings>? _behaviorMonitor;

    private readonly IOptionsMonitor<PeerSettings>? _peerSettingsMonitor;

    private readonly IOptionsMonitor<EncryptionSettings>? _encryptionMonitor;

    private readonly IOptionsMonitor<ConnectionSettings>? _connectionMonitor;

    private readonly IOptionsMonitor<WebSeedSettings>? _webSeedMonitor;

    private readonly IOptionsMonitor<ProxySettings>? _proxyMonitor;

    private readonly IOptionsMonitor<PrivacySettings>? _privacyMonitor;

    private I2pService? _i2pService;

    private readonly IOptionsMonitor<I2pSettings>? _i2pSettingsMonitor;

    private readonly vTorrent.Core.Network.IpFilter.IpFilter? _ipFilter;

    private readonly vTorrent.Core.Network.PeerClass.PeerClassManager? _peerClassManager;

    private readonly IOptionsMonitor<DiskSettings>? _diskMonitor;

    private readonly UnchokeAllocator? _unchokeAllocator;

    private readonly Network.UdpSocketManager? _udpSocketManager;
    private readonly TrackerCommunication.Udp.UdpTrackerPacketHandler? _trackerPacketHandler;

    public EngineFactory(

        ILoggerFactory loggerFactory,

        AlertManager alertManager,

        ResourceAllocator resourceAllocator,

        PeerSettings? peerSettings = null,

        IOptionsMonitor<TrackerSettings>? trackerMonitor = null,

        ITorrentDialog? torrentDialog = null,

        GlobalBandwidthCoordinator? bandwidthCoordinator = null,

        TorrentDatabase? database = null,

        ITransportConnector? transportConnector = null,

        IExternalIpVoter? externalIpVoter = null,

        IOptionsMonitor<BehaviorSettings>? behaviorMonitor = null,

        IOptionsMonitor<PeerSettings>? peerSettingsMonitor = null,

        IOptionsMonitor<EncryptionSettings>? encryptionMonitor = null,

        IOptionsMonitor<ConnectionSettings>? connectionMonitor = null,

        IOptionsMonitor<WebSeedSettings>? webSeedMonitor = null,

        IOptionsMonitor<PrivacySettings>? privacyMonitor = null,

        I2pService? i2pService = null,

        IOptionsMonitor<I2pSettings>? i2pSettingsMonitor = null,

        vTorrent.Core.Network.IpFilter.IpFilter? ipFilter = null,

        vTorrent.Core.Network.PeerClass.PeerClassManager? peerClassManager = null,

        IOptionsMonitor<DiskSettings>? diskMonitor = null,

        UnchokeAllocator? unchokeAllocator = null,

        Network.UdpSocketManager? udpSocketManager = null,

        TrackerCommunication.Udp.UdpTrackerPacketHandler? trackerPacketHandler = null,

        IOptionsMonitor<ProxySettings>? proxyMonitor = null)

    {

        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

        _alertManager = alertManager ?? throw new ArgumentNullException(nameof(alertManager));

        _resourceAllocator = resourceAllocator ?? throw new ArgumentNullException(nameof(resourceAllocator));

        _bandwidthCoordinator = bandwidthCoordinator;

        _defaultPeerSettings = peerSettings ?? new PeerSettings();

        _trackerMonitor = trackerMonitor ?? new OptionsMonitorShim<TrackerSettings>(new TrackerSettings());

        _torrentDialog = torrentDialog;

        _database = database;

        _transportConnector = transportConnector;

        _externalIpVoter = externalIpVoter;

        _behaviorMonitor = behaviorMonitor;

        _peerSettingsMonitor = peerSettingsMonitor;

        _encryptionMonitor = encryptionMonitor;

        _connectionMonitor = connectionMonitor;

        _webSeedMonitor = webSeedMonitor;

        _privacyMonitor = privacyMonitor;

        _proxyMonitor = proxyMonitor;

        _i2pService = i2pService;

        _i2pSettingsMonitor = i2pSettingsMonitor;

        _ipFilter = ipFilter;

        _peerClassManager = peerClassManager;

        _diskMonitor = diskMonitor;

        _unchokeAllocator = unchokeAllocator;

        _udpSocketManager = udpSocketManager;
        _trackerPacketHandler = trackerPacketHandler;

    }

    /// <summary>
    /// Sets the I2P service after construction (created in orchestrator's InitializeAsync).
    /// </summary>
    internal void SetI2pService(I2pService? service) => _i2pService = service;

    /// <summary>

    /// Create a new TorrentEngine for a managed torrent

    /// </summary>

    /// <param name="managed">The managed torrent to create engine for</param>

    /// <param name="settings">Engine-specific settings</param>

    /// <returns>Configured TorrentEngine ready to start</returns>

    public TorrentEngine Create(ManagedTorrent managed, EngineSettings? settings = null)

    {

        ArgumentNullException.ThrowIfNull(managed);

        if (managed.Torrent == null)

            throw new InvalidOperationException($"Cannot create engine for torrent without metadata: {managed.InfoHash}");

        settings ??= EngineSettings.FromManagedTorrent(managed);

        // Create peer settings (copy defaults and apply per-torrent settings)

        var peerSettings = CreatePeerSettings(managed, settings);

        // Create bandwidth limiter for this torrent

        IPeerBandwidthLimiter? bandwidthLimiter = CreateBandwidthLimiter(managed, settings);

        // I2P-aware transport selection
        ITransportConnector? effectiveConnector = _transportConnector;

        if (managed.IsI2p && _i2pService?.TransportConnector != null)
        {
            if (_i2pSettingsMonitor?.CurrentValue.AllowMixedMode == true && _transportConnector != null)
            {
                effectiveConnector = new CompositeTransportConnector(_transportConnector, _i2pService.TransportConnector);
            }
            else
            {
                effectiveConnector = _i2pService.TransportConnector;
            }
        }

        // Create the engine

        var engine = new TorrentEngine(

            managed.Torrent,

            managed.SavePath,

            peerSettings,

            _trackerMonitor,

            _loggerFactory,

            _torrentDialog ?? new NullTorrentDialog(),

            settings.SequentialDownload,

            bandwidthLimiter,

            _database,

            transferAccumulator: managed.Statistics as ITransferAccumulator,

            transportConnector: effectiveConnector,

            externalIpVoter: _externalIpVoter,

            behaviorMonitor: _behaviorMonitor,

            peerSettingsMonitor: _peerSettingsMonitor,

            encryptionMonitor: _encryptionMonitor,

            connectionMonitor: _connectionMonitor,

            webSeedMonitor: _webSeedMonitor,

            privacyMonitor: _privacyMonitor,

            diskMonitor: _diskMonitor,

            peerClassManager: _peerClassManager,

            unchokeAllocator: _unchokeAllocator,

            udpSocketManager: _udpSocketManager,

            trackerPacketHandler: _trackerPacketHandler,

            i2pSettingsMonitor: _i2pSettingsMonitor,

            proxyMonitor: _proxyMonitor);

        engine.ManagedTorrentInternal = managed;
        engine.I2pServiceInternal = _i2pService;

        // Wire events to alert manager

        WireEngineEvents(engine, managed);

        return engine;

    }

    private IPeerBandwidthLimiter? CreateBandwidthLimiter(ManagedTorrent managed, EngineSettings settings)

    {

        // Get per-torrent limits from settings or use defaults from resource allocator

        int downloadLimit = settings.DownloadLimit > 0

            ? settings.DownloadLimit

            : _resourceAllocator.Bandwidth.DefaultPerTorrentDownloadLimit;

        int uploadLimit = settings.UploadLimit > 0

            ? settings.UploadLimit

            : _resourceAllocator.Bandwidth.DefaultPerTorrentUploadLimit;

        // If we have a global bandwidth coordinator, use it for coordinated limiting

        if (_bandwidthCoordinator != null)

        {

            var torrentLimiter = _bandwidthCoordinator.GetOrCreateLimiter(

                managed.InfoHash,

                downloadLimit,

                uploadLimit);

            // OPTIMIZATION: If effective limits are 0 (unlimited), skip bandwidth limiting entirely

            // This avoids all quota tracking overhead for unlimited torrents (libtorrent does this too)

            int effectiveDownload = torrentLimiter.EffectiveDownloadLimit;

            int effectiveUpload = torrentLimiter.EffectiveUploadLimit;

            if (effectiveDownload == 0 && effectiveUpload == 0)

            {

                // Completely unlimited - no bandwidth limiter needed

                return null;

            }

            // Create adapter that bridges TorrentBandwidthLimiter to IPeerBandwidthLimiter

            return new PeerBandwidthLimiterAdapter(

                effectiveDownload,

                effectiveUpload,

                requestDownload: (priority, bytes) => torrentLimiter.RequestDownload(

                    new SimpleBandwidthConsumer($"factory_{managed.InfoHash}"),

                    bytes,

                    priority),

                requestUpload: (priority, bytes) => torrentLimiter.RequestUpload(

                    new SimpleBandwidthConsumer($"factory_{managed.InfoHash}"),

                    bytes,

                    priority));

        }

        // If no coordinator, use a simple standalone rate limiter

        if (downloadLimit > 0 || uploadLimit > 0)

        {

            return new SimpleRateLimiter(downloadLimit, uploadLimit);

        }

        return null;

    }

    /// <summary>

    /// Simple bandwidth consumer for quota requests.

    /// </summary>

    private class SimpleBandwidthConsumer : IBandwidthConsumer

    {

        public string Id { get; }

        public bool IsDisconnecting => false;

        public SimpleBandwidthConsumer(string id)

        {

            Id = id;

        }

        public void OnBandwidthAssigned(BandwidthChannelType channel, int amount)

        {

            // Not used in this context

        }

    }

    private PeerSettings CreatePeerSettings(ManagedTorrent managed, EngineSettings settings)

    {

        // Start with defaults, override per-torrent connection limits

        var peerSettings = new PeerSettings

        {

            MaxConnections = settings.MaxConnections > 0

                ? settings.MaxConnections

                : _resourceAllocator.Connections.MaxConnectionsPerTorrent,

            MaxUploadsPerTorrent = _resourceAllocator.Connections.MaxUploadsPerTorrent,

            ConnectTimeout = _defaultPeerSettings.ConnectTimeout,

            HandshakeTimeout = _defaultPeerSettings.HandshakeTimeout,

            ListenPort = _defaultPeerSettings.ListenPort,

            MaxPendingBlocksPerPeer = _defaultPeerSettings.MaxPendingBlocksPerPeer,

            EnablePex = _defaultPeerSettings.EnablePex,

            PeerId = _defaultPeerSettings.PeerId,

            ClientVersion = _defaultPeerSettings.ClientVersion,

            InactivityTimeout = _defaultPeerSettings.InactivityTimeout,

            PieceTimeout = _defaultPeerSettings.PieceTimeout,

            UnchokeInterval = _defaultPeerSettings.UnchokeInterval,

            OptimisticUnchokeInterval = _defaultPeerSettings.OptimisticUnchokeInterval,

            PrioritizePartialPieces = _defaultPeerSettings.PrioritizePartialPieces,

            StrictEndgameMode = _defaultPeerSettings.StrictEndgameMode,

            CloseRedundantConnections = _defaultPeerSettings.CloseRedundantConnections,

            SeedingOutgoingConnections = _defaultPeerSettings.SeedingOutgoingConnections,

            DiskCacheSize = _defaultPeerSettings.DiskCacheSize,
            SendBufferWatermark = _defaultPeerSettings.SendBufferWatermark,
            SendBufferLowWatermark = _defaultPeerSettings.SendBufferLowWatermark,
            SendBufferWatermarkFactor = _defaultPeerSettings.SendBufferWatermarkFactor

        };

        return peerSettings;

    }

    private void WireEngineEvents(TorrentEngine engine, ManagedTorrent managed)

    {

        var infoHash = managed.InfoHash;

        var name = managed.Name;

        // Progress updates (throttled - only major milestones)

        int lastReportedPercent = -1;

        engine.ProgressChanged += (s, e) =>

        {

            int percent = (int)(e.Progress * 100);

            // Only post alerts on 10% milestones or completion

            if (percent != lastReportedPercent && (percent % 10 == 0 || percent == 100))

            {

                lastReportedPercent = percent;

                // Progress alerts can be very noisy, so we use normal priority

            }

        };

        // Download completed

        engine.DownloadCompleted += (s, e) =>

        {

            _alertManager.Post(new TorrentFinishedAlert(infoHash, name));

        };

        // Missing files detected
        engine.MissingFilesDetected += (sender, e) =>
        {
            var current = managed.GetStatus();
            managed.UpdateStatus(current with { MissingFiles = true }, force: true);
        };

        // Peers discovered

        engine.PeersDiscovered += (s, e) =>

        {

            _alertManager.Post(new TrackerAnnounceAlert(

                infoHash,

                e.TrackerUrl,

                e.Seeders,

                e.Leechers,

                e.Peers.Count));

        };

        // Integrity verification

        engine.IntegrityVerificationCompleted += (s, e) =>

        {

            var result = e.Result;

            _alertManager.Post(new CheckingCompleteAlert(

                infoHash,

                result.VerifiedPieces?.Count ?? 0,

                result.CorruptPieces?.Count ?? 0,

                result.TotalPieces));

        };

    }

}

/// <summary>

/// Null implementation of ITorrentDialog for when no dialog is needed

/// </summary>

internal class NullTorrentDialog : ITorrentDialog

{

    public void Publish<TEvent>(TEvent @event) where TEvent : notnull { }

    public void Subscribe<TEvent>(Action<object?, TEvent> handler) { }

    public void Unsubscribe<TEvent>(Action<object?, TEvent> handler) { }

}

/// <summary>
/// Minimal IOptionsMonitor shim that wraps a static value.
/// Used as fallback when no DI-provided monitor is available.
/// </summary>
internal sealed class OptionsMonitorShim<T> : IOptionsMonitor<T>
{
    public OptionsMonitorShim(T value) => CurrentValue = value;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
