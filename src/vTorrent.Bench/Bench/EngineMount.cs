using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using vTorrent.Abstractions.Settings;
using vTorrent.Bench.Config;
using vTorrent.Bench.Settings;
using vTorrent.Bench.Simulation;
using vTorrent.Core.Download;
using vTorrent.Core.Engine;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Utilities;
using vTorrent.Core.Session;
using vTorrent.Core.Upload;

namespace vTorrent.Bench.Bench;

/// <summary>
/// Bolts real Core engine components (DownloadCoordinator, ChokingManager, etc.)
/// onto faked edges (FakePeerManager, MemoryPieceManager, MutableSettingsMonitors).
/// The "engine mount" metaphor: real engine, test-bench frame.
/// </summary>
public sealed class EngineMount : IDisposable
{
    // --- Faked edges ---
    public SyntheticTorrent SyntheticTorrent { get; }
    public FakePeerManager PeerManager { get; }
    public FakePeerRegistry PeerRegistry { get; }
    public MemoryPieceManager PieceManager { get; }
    public Bitfield LocalBitfield { get; }

    // --- Settings monitors (mutable for live tuning) ---
    public MutableSettingsMonitor<BandwidthSettings> BandwidthMonitor { get; }
    public MutableSettingsMonitor<ConnectionSettings> ConnectionMonitor { get; }
    public MutableSettingsMonitor<QueueSettings> QueueMonitor { get; }
    public MutableSettingsMonitor<BehaviorSettings> BehaviorMonitor { get; }
    public MutableSettingsMonitor<PeerSettings> PeerMonitor { get; }
    public MutableSettingsMonitor<DiskSettings> DiskMonitor { get; }

    // --- Real Core components ---
    public TorrentStatistics Statistics { get; }
    public EndgameManager EndgameManager { get; }
    public DownloadCoordinator DownloadCoordinator { get; }
    public ChokingManager ChokingManager { get; }
    public UploadCoordinator UploadCoordinator { get; }
    public PeerReplacer PeerReplacer { get; }
    public PeerMessageRouter MessageRouter { get; }

    // --- Derived ---
    public byte[] InfoHash { get; }
    public ScenarioConfig Config { get; }

    private CancellationTokenSource? _cts;
    private bool _disposed;

    public EngineMount(ScenarioConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));

        // 1. Generate synthetic torrent (real .torrent support deferred)
        if (!string.IsNullOrEmpty(config.TorrentFilePath))
            throw new NotImplementedException("Real .torrent loading is not yet supported in bench mode.");

        SyntheticTorrent = SyntheticTorrent.Generate(config.PieceCount, config.PieceSize);
        var torrentInfo = SyntheticTorrent.Info;

        // Deterministic info hash from torrent name
        InfoHash = SHA1.HashData(Encoding.UTF8.GetBytes(torrentInfo.Name));

        // 2. Create faked edges
        PeerManager = new FakePeerManager(config, SyntheticTorrent);
        PeerRegistry = new FakePeerRegistry(PeerManager);
        PieceManager = new MemoryPieceManager(torrentInfo);
        LocalBitfield = new Bitfield(torrentInfo.PieceCount);

        // 3. Create mutable settings monitors (6 types)
        BandwidthMonitor = new MutableSettingsMonitor<BandwidthSettings>();
        ConnectionMonitor = new MutableSettingsMonitor<ConnectionSettings>();
        QueueMonitor = new MutableSettingsMonitor<QueueSettings>();
        BehaviorMonitor = new MutableSettingsMonitor<BehaviorSettings>();
        PeerMonitor = new MutableSettingsMonitor<PeerSettings>();
        DiskMonitor = new MutableSettingsMonitor<DiskSettings>();

        var loggerFactory = NullLoggerFactory.Instance;

        // 4. Create real Core components
        Statistics = new TorrentStatistics(loggerFactory.CreateLogger<TorrentStatistics>());

        EndgameManager = new EndgameManager(loggerFactory.CreateLogger<EndgameManager>());

        // DownloadCoordinator takes PeerSettings directly (not IOptionsMonitor)
        DownloadCoordinator = new DownloadCoordinator(
            peerManager: PeerManager,
            pieceManager: PieceManager,
            statisticsTracker: Statistics,
            endgameStrategy: EndgameManager,
            localBitfield: LocalBitfield,
            torrentInfo: torrentInfo,
            settings: PeerMonitor.CurrentValue,
            peerRegistry: PeerRegistry,
            logger: loggerFactory.CreateLogger<DownloadCoordinator>(),
            diskWriteCache: null,
            behaviorMonitor: BehaviorMonitor,
            diskMonitor: DiskMonitor,
            webSeedMonitor: null);

        ChokingManager = new ChokingManager(
            peerManager: PeerManager,
            statisticsTracker: Statistics,
            isSeedingFunc: () => LocalBitfield.IsComplete,
            logger: loggerFactory.CreateLogger<ChokingManager>(),
            behaviorMonitor: BehaviorMonitor,
            peerSettingsMonitor: PeerMonitor,
            torrentSettings: null,
            getTotalPiecesFunc: () => torrentInfo.PieceCount,
            getPeerPieceCountFunc: null,
            unchokeAllocator: null);

        UploadCoordinator = new UploadCoordinator(
            peerManager: PeerManager,
            pieceManager: PieceManager,
            chokingManager: ChokingManager,
            statisticsTracker: Statistics,
            torrentInfo: torrentInfo,
            hasPiece: pieceIndex => PieceManager.IsPieceComplete(pieceIndex),
            logger: loggerFactory.CreateLogger<UploadCoordinator>());

        PeerReplacer = new PeerReplacer(
            peerManager: PeerManager,
            statisticsTracker: Statistics,
            isSeeding: () => LocalBitfield.IsComplete,
            logger: loggerFactory.CreateLogger<PeerReplacer>(),
            behaviorMonitor: BehaviorMonitor);

        MessageRouter = new PeerMessageRouter(
            peerManager: PeerManager,
            logger: loggerFactory.CreateLogger<PeerMessageRouter>());

        // 5. Register message handlers (mirrors TorrentEngine Phase 6)
        DownloadCoordinator.RegisterHandlers(MessageRouter);
        UploadCoordinator.RegisterHandlers(MessageRouter);
        ChokingManager.RegisterHandlers(MessageRouter);

        // 6. Register all synthetic peers with statistics tracker
        foreach (var peer in PeerManager.ConnectedPeers)
            Statistics.RegisterPeer(peer);
    }

    // --- Lifecycle ---

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts != null)
            throw new InvalidOperationException("EngineMount is already running.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Start peer simulation (connects synthetic peers, starts choke timers)
        await PeerManager.StartAsync(_cts.Token).ConfigureAwait(false);

        // Start choking manager (periodic rechoking loop)
        await ChokingManager.StartAsync(_cts.Token).ConfigureAwait(false);

        // Start the download loop
        await DownloadCoordinator.StartAsync(_cts.Token).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_cts == null)
            return;

        _cts.Cancel();

        // Stop in reverse order
        try { await DownloadCoordinator.StopAsync().ConfigureAwait(false); } catch { /* swallow */ }
        try { await ChokingManager.StopAsync().ConfigureAwait(false); } catch { /* swallow */ }
        try { await PeerReplacer.StopAsync().ConfigureAwait(false); } catch { /* swallow */ }
        try { await PeerManager.StopAsync().ConfigureAwait(false); } catch { /* swallow */ }

        _cts.Dispose();
        _cts = null;
    }

    // --- Settings Registry Factory ---

    public SettingsRegistry BuildSettingsRegistry()
    {
        return SettingsRegistry.Build(
            BandwidthMonitor, ConnectionMonitor, QueueMonitor,
            BehaviorMonitor, PeerMonitor, DiskMonitor);
    }

    // --- IDisposable ---

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();

        MessageRouter.Dispose();
        UploadCoordinator.Dispose();
        ChokingManager.Dispose();
        DownloadCoordinator.Dispose();
        PeerManager.Dispose();
    }
}
