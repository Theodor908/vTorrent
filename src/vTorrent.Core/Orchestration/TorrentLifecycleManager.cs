using System;

using System.Collections;

using System.Collections.Concurrent;

using System.Collections.Generic;

using System.IO;

using System.Linq;

using System.Threading;

using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using vTorrent.Bencode.Objects;

using vTorrent.Bencode.Parsers;

using vTorrent.Bencode.Torrents;

using vTorrent.Core.Orchestration.Alerts;

using vTorrent.Core.Persistence;

using vTorrent.Core.ResumeData;

using vTorrent.Core.Session;

using vTorrent.Storage;

using vTorrent.Core.PeerCommunication.Models;

using vTorrent.Core.PeerCommunication.Transport;

using vTorrent.Core.PieceIO;
using vTorrent.Core.PieceIO.Backends;

using vTorrent.Core;

using vTorrent.Core.Merkle;

using vTorrent.Core.State;

using vTorrent.Abstractions.Enums;

using vTorrent.Abstractions.Interfaces.Storage;

using vTorrent.Abstractions.Models;

using vTorrent.Abstractions.Records;
using vTorrent.Core.Engine;
using vTorrent.Core.IO;
using vTorrent.Core.PeerCommunication.Identification;

namespace vTorrent.Core.Orchestration;

/// <summary>

/// Manages torrent lifecycle operations: add, remove, start, pause, recheck, move.

/// Extracted from TorrentOrchestrator to follow SRP.

/// Accesses shared state via orchestrator back-reference.

/// </summary>

internal class TorrentLifecycleManager

{

    private readonly TorrentOrchestrator _orch;

    private readonly ILogger<TorrentLifecycleManager> _logger;

    private readonly ISecureFileWiper _secureFileWiper;

    private readonly DeletionWorker _deletionWorker;

    private static bool _ssdWarningShown;

    public TorrentLifecycleManager(TorrentOrchestrator orchestrator, ILoggerFactory loggerFactory, ISecureFileWiper secureFileWiper, DeletionWorker deletionWorker)

    {

        _orch = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

        _logger = loggerFactory.CreateLogger<TorrentLifecycleManager>();

        _secureFileWiper = secureFileWiper ?? throw new ArgumentNullException(nameof(secureFileWiper));

        _deletionWorker = deletionWorker ?? throw new ArgumentNullException(nameof(deletionWorker));

    }

    /// <summary>
    /// True only when the torrent is actually transferring: Phase says
    /// Downloading/Seeding AND Intent is Active. Under the orthogonal state model
    /// a paused torrent keeps its phase, so phase alone no longer means "running".
    /// Lifecycle guards that mean "already running — nothing to start" must use this.
    /// </summary>
    internal static bool IsActivelyTransferring(TorrentStatus status) =>
        status.Phase is TransferPhase.Downloading or TransferPhase.Seeding
        && status.Intent == UserIntent.Active;

    /// <summary>Set user intent on a managed torrent via the state controller.</summary>

    private void SetIntent(ManagedTorrent managed, UserIntent intent)

    {

        var trigger = intent switch
        {
            UserIntent.Active => IntentTrigger.Activate,
            UserIntent.Paused => IntentTrigger.Pause,
            UserIntent.Queued => IntentTrigger.Queue,
            _ => throw new ArgumentOutOfRangeException(nameof(intent))
        };

        managed.StateController.PostIntent(trigger);

    }

    /// <summary>Set error on a managed torrent via the state controller.</summary>

    private void SetHealth(ManagedTorrent managed, string errorMessage)

    {

        managed.StateController.PostError(new TorrentError { Message = errorMessage });

    }

    /// <summary>Clear error on a managed torrent via the state controller.</summary>

    private void ClearHealth(ManagedTorrent managed)

    {

        managed.StateController.PostClearError();

    }

    /// <summary>
    /// Notify the orchestrator that a torrent's status changed.
    /// Now a no-op: the StateController's StatusChanged subscription (wired in
    /// TorrentOrchestrator.WireEngineEvents) handles state index, statistics, events,
    /// persistence, and auto-manager trigger via ChannelDrained.
    /// Kept to avoid a large call-site diff; will be removed in Task 14.
    /// </summary>

    private void NotifyStatusChanged(ManagedTorrent managed, TorrentStatus oldStatus)

    {

        // No-op: controller subscription handles all side-effects.

    }

    #region Add Torrent

    public async Task<TorrentHandle> AddTorrentAsync(

        string torrentFilePath,

        string? savePath = null,

        bool startImmediately = true,

        CancellationToken cancellationToken = default)

    {

        var bytes = await File.ReadAllBytesAsync(torrentFilePath, cancellationToken).ConfigureAwait(false);

        return await AddTorrentFromBytesAsync(bytes, savePath, startImmediately, torrentFilePath, cancellationToken).ConfigureAwait(false);

    }

    public async Task<TorrentHandle> AddTorrentFromBytesAsync(

        byte[] torrentBytes,

        string? savePath = null,

        bool startImmediately = true,

        string? torrentFilePath = null,

        CancellationToken cancellationToken = default)

    {

        if (_orch.IsShuttingDown)

            throw new InvalidOperationException("Orchestrator is shutting down");

        var parser = new BencodeParser();

        var parsed = parser.Parse(torrentBytes, out _);

        if (parsed is not BDictionary dict)

            throw new InvalidDataException("Invalid torrent file format");

        var torrent = TorrentParser.FromBDictionary(dict);

        var infoHash = torrent.GetInfoHashHex();

        _logger.LogInformation("Adding torrent: {Name} ({InfoHash})", torrent.Info.Name, infoHash);

        if (_orch.TorrentsInternal.Exists(infoHash))

        {

            _logger.LogWarning("Torrent {InfoHash} already exists in memory", infoHash);

            return new TorrentHandle(_orch.TorrentsInternal.Find(infoHash)!);

        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var effectiveSavePath = savePath ?? _orch.Persistence.Settings.Disk.DefaultSavePath;

        var queuePosition = await _orch.Persistence.GetNextQueuePositionAsync().ConfigureAwait(false);

        var torrentsDir = Path.Combine(_orch.Persistence.DataDirectory, "torrents");

        Directory.CreateDirectory(torrentsDir);

        var persistentTorrentPath = Path.Combine(torrentsDir, $"{infoHash}.torrent");

        await File.WriteAllBytesAsync(persistentTorrentPath, torrentBytes, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Saved torrent file to: {Path}", persistentTorrentPath);

        var record = new TorrentRecord

        {

            InfoHash = infoHash,

            Name = torrent.Info.Name,

            Comment = torrent.Comment,

            CreatedBy = torrent.CreatedBy,

            TotalSize = torrent.TotalSize,

            PieceCount = torrent.Info.Pieces.Count,

            PieceSize = (int)torrent.Info.PieceLength,

            FileCount = torrent.Info.Files.Count,

            IsPrivate = torrent.Info.IsPrivate,

            SavePath = effectiveSavePath,

            TorrentFilePath = persistentTorrentPath,

            UserIntent = startImmediately ? "Queued" : "Paused",

            AddedAt = now,

            QueuePosition = queuePosition

        };

        var trackers = new List<(string Url, int Tier)>();

        if (torrent.AnnounceList != null)

        {

            for (int tierIndex = 0; tierIndex < torrent.AnnounceList.Count; tierIndex++)

            {

                foreach (var url in torrent.AnnounceList[tierIndex])

                {

                    trackers.Add((url, tierIndex));

                }

            }

        }

        else if (!string.IsNullOrEmpty(torrent.Announce))

        {

            trackers.Add((torrent.Announce, 0));

        }

        var files = torrent.Info.Files.Select((f, index) => new FileRecord

        {

            InfoHash = infoHash,

            FileIndex = index,

            Path = string.Join("/", f.Path),

            Size = f.Length,

            Priority = 4

        });

        var inserted = await _orch.Persistence.TrySaveNewTorrentAsync(record, trackers, files).ConfigureAwait(false);

        if (!inserted)

        {

            try { File.Delete(persistentTorrentPath); } catch { /* ignore */ }

            if (_orch.TorrentsInternal.Exists(infoHash))

            {

                _logger.LogWarning("Torrent {InfoHash} was added concurrently (now in memory)", infoHash);

                return new TorrentHandle(_orch.TorrentsInternal.Find(infoHash)!);

            }

            throw new InvalidOperationException($"Torrent {infoHash} already exists in database");

        }

        var managed = new ManagedTorrent(infoHash, torrent.Info.Name)

        {

            Torrent = torrent,

            SavePath = effectiveSavePath,

            TorrentFilePath = persistentTorrentPath,

            QueuePosition = queuePosition,

            UserPaused = !startImmediately,

            ResumeData = new TorrentResumeData

            {

                InfoHash = infoHash,

                Name = torrent.Info.Name,

                Comment = torrent.Comment,

                CreatedBy = torrent.CreatedBy,

                PieceCount = torrent.Info.Pieces.Count,

                PieceLength = (int)torrent.Info.PieceLength,

                SavePath = effectiveSavePath,

                TorrentFilePath = persistentTorrentPath,

                AddedTime = now,

                IsPaused = !startImmediately,

                UserPaused = !startImmediately,

                Trackers = torrent.AnnounceList?.Select(tier => tier.ToList()).ToList()

            },

            Statistics = new TorrentStatistics

            {

                AddedTime = DateTime.UtcNow,

                TotalSize = torrent.TotalSize,

                TotalWanted = torrent.TotalSize,

                TotalPieces = torrent.Info.Pieces.Count

            }

        };

        // Dual-write: set initial TorrentStatus

        SetIntent(managed, startImmediately ? UserIntent.Queued : UserIntent.Paused);

        // BEP 52: Build merkle trees for v2/hybrid torrents

        managed.MerkleTrees = await LoadOrBuildMerkleTreesAsync(

            torrent, infoHash, cancellationToken).ConfigureAwait(false);

        _orch.TorrentsInternal.Add(managed);

        _orch.StateIndex.Add(managed);

        _orch.QueueManager.Add(managed);

        // Register save path with disk space monitor for proactive space tracking
        _orch.DiskSpaceMonitorInternal.RegisterPath(effectiveSavePath);

        await _orch.Persistence.SaveResumeDataAsync(infoHash, managed.ResumeData).ConfigureAwait(false);

        _orch.RaiseTorrentAdded(infoHash, torrent.Info.Name);

        if (startImmediately)

        {

            _orch.AutoManager.Trigger();

        }

        _logger.LogInformation("Torrent added: {Name}", torrent.Info.Name);

        return new TorrentHandle(managed);

    }

    #endregion

    #region Add Magnet Link

    public async Task<TorrentHandle> AddMagnetLinkAsync(

        string magnetUri,

        string? savePath = null,

        bool startImmediately = true,

        CancellationToken cancellationToken = default)

    {

        if (_orch.IsShuttingDown)

            throw new InvalidOperationException("Orchestrator is shutting down");

        if (string.IsNullOrWhiteSpace(magnetUri))

            throw new ArgumentException("Magnet URI cannot be null or empty", nameof(magnetUri));

        var magnetLink = MagnetLink.Parse(magnetUri);

        var infoHash = magnetLink.InfoHashHex;

        var displayName = magnetLink.DisplayName ?? $"Magnet-{infoHash.Substring(0, 8)}";

        _logger.LogInformation("Adding magnet link: {Name} ({InfoHash})", displayName, infoHash);

        if (_orch.TorrentsInternal.Exists(infoHash))

        {

            _logger.LogWarning("Torrent {InfoHash} already exists in memory", infoHash);

            return new TorrentHandle(_orch.TorrentsInternal.Find(infoHash)!);

        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var effectiveSavePath = savePath ?? _orch.Persistence.Settings.Disk.DefaultSavePath;

        var queuePosition = await _orch.Persistence.GetNextQueuePositionAsync().ConfigureAwait(false);

        var record = new TorrentRecord

        {

            InfoHash = infoHash,

            Name = displayName,

            TotalSize = magnetLink.ExactLength ?? 0,

            PieceCount = 0,

            PieceSize = 0,

            FileCount = 0,

            IsPrivate = false,

            SavePath = effectiveSavePath,

            TorrentFilePath = null,

            UserIntent = startImmediately ? "Active" : "Paused",

            TransferPhase = startImmediately ? "FetchingMetadata" : null,

            AddedAt = now,

            QueuePosition = queuePosition,

            IsMagnetLink = true,

            MagnetUri = magnetUri

        };

        var trackers = magnetLink.Trackers

            .Select((url, index) => (url, index))

            .ToList();

        var inserted = await _orch.Persistence.TrySaveNewTorrentAsync(record, trackers, Enumerable.Empty<FileRecord>()).ConfigureAwait(false);

        if (!inserted)

        {

            if (_orch.TorrentsInternal.Exists(infoHash))

            {

                _logger.LogWarning("Torrent {InfoHash} was added concurrently (now in memory)", infoHash);

                return new TorrentHandle(_orch.TorrentsInternal.Find(infoHash)!);

            }

            throw new InvalidOperationException($"Torrent {infoHash} already exists in database");

        }

        var managed = new ManagedTorrent(infoHash, displayName)

        {

            Torrent = null,

            SavePath = effectiveSavePath,

            TorrentFilePath = null,

            QueuePosition = queuePosition,

            UserPaused = !startImmediately,

            IsMagnetLink = true,

            MagnetLinkData = magnetLink,

            InfoHashBytes = magnetLink.InfoHash,

            ResumeData = new TorrentResumeData

            {

                InfoHash = infoHash,

                Name = displayName,

                PieceCount = 0,

                PieceLength = 0,

                SavePath = effectiveSavePath,

                AddedTime = now,

                IsPaused = !startImmediately,

                UserPaused = !startImmediately,

                Trackers = magnetLink.Trackers.Count > 0

                    ? new List<List<string>> { magnetLink.Trackers.ToList() }

                    : null

            },

            Statistics = new TorrentStatistics

            {

                AddedTime = DateTime.UtcNow,

                TotalSize = magnetLink.ExactLength ?? 0,

                TotalWanted = magnetLink.ExactLength ?? 0,

                TotalPieces = 0

            }

        };

        managed.MetadataReceived += OnMagnetMetadataReceived;

        // Dual-write: set initial TorrentStatus for magnet link

        if (startImmediately)

        {

            managed.StateController.PostIntent(IntentTrigger.Activate);

            managed.StateController.PostPhase(PhaseTrigger.FetchMetadata);

        }

        else

        {

            SetIntent(managed, UserIntent.Paused);

        }

        _orch.TorrentsInternal.Add(managed);

        _orch.StateIndex.Add(managed);

        _orch.QueueManager.Add(managed);

        // Register save path with disk space monitor for proactive space tracking
        _orch.DiskSpaceMonitorInternal.RegisterPath(effectiveSavePath);

        if (_orch.DhtCoordinator.IsDhtRunning && managed.InfoHashBytes != null)

        {

            _orch.DhtCoordinator.RegisterTorrentWithDht(managed);

            _logger.LogDebug("Registered magnet link with DHT: {InfoHash}", infoHash);

        }

        _orch.RaiseTorrentAdded(infoHash, displayName);

        if (startImmediately)

        {

            await StartMetadataDownloadAsync(managed, cancellationToken).ConfigureAwait(false);

        }

        _logger.LogInformation("Magnet link added: {Name} (waiting for metadata)", displayName);

        return new TorrentHandle(managed);

    }

    private async Task StartMetadataDownloadAsync(ManagedTorrent managed, CancellationToken cancellationToken)

    {

        if (managed.InfoHashBytes == null)

        {

            _logger.LogError("Cannot start metadata download: no info hash");

            return;

        }

        var coordinatorLogger = _orch.LoggerFactoryInternal.CreateLogger<MetadataDownloadCoordinator>();

        var coordinator = new MetadataDownloadCoordinator(

            coordinatorLogger,

            _orch.LoggerFactoryInternal,

            managed.InfoHashBytes,

            _orch.CreatePeerSettings(),

            _orch.TransportConnectorInternal,

            managed,

            // RUNTIME: use monitor for live settings, fallback to persistence
            (_orch.PeerMonitorInternal?.CurrentValue ?? _orch.Persistence.Settings.Peer).RequestTimeout);

        coordinator.MetadataReceived += metadata =>

        {

            OnCoordinatorMetadataReceived(managed, metadata);

        };

        coordinator.MetadataFailed += error =>

        {

            _logger.LogWarning("Metadata download failed for {InfoHash}: {Error}", managed.InfoHash, error);

        };

        coordinator.ProgressChanged += (received, total) =>

        {

            managed.MetadataPiecesReceived = received;

            managed.MetadataPiecesTotal = total;

            managed.MetadataProgress = total > 0 ? (double)received / total : 0;

        };

        lock (_orch.MetadataCoordinatorLock)

        {

            _orch.MetadataCoordinators[managed.InfoHash] = coordinator;

        }

        coordinator.Start();

        if (managed.MagnetLinkData?.Peers != null)

        {

            var initialPeers = managed.MagnetLinkData.Peers

                .Select(ep => PeerInfo.FromEndPoint(ep, source: "magnet"))

                .ToList();

            coordinator.AddPeers(initialPeers);

        }

        if (_orch.DhtCoordinator.IsDhtRunning)

        {

            _orch.DhtCoordinator.DhtManager!.PeersDiscovered += (infoHash, peers) =>

            {

                if (Convert.ToHexString(infoHash) == managed.InfoHash)

                {

                    coordinator.AddPeers(peers);

                }

            };

        }

        await AnnounceToTrackersForMetadataAsync(managed, coordinator, cancellationToken).ConfigureAwait(false);

        // Start metadata timeout monitor

        // RUNTIME: use monitor for live settings, fallback to persistence
        var timeoutMinutes = (_orch.BehaviorMonitorInternal?.CurrentValue ?? _orch.Persistence.Settings.Behavior).MetadataDownloadTimeoutMinutes;

        if (timeoutMinutes > 0)

        {

            _ = MonitorMetadataTimeoutAsync(managed, TimeSpan.FromMinutes(timeoutMinutes), cancellationToken);

        }

        _logger.LogDebug("Started metadata download for {InfoHash}", managed.InfoHash);

    }

    private async Task MonitorMetadataTimeoutAsync(ManagedTorrent managed, TimeSpan timeout, CancellationToken ct)

    {

        try

        {

            await Task.Delay(timeout, ct).ConfigureAwait(false);

            if (managed.GetStatus().Phase == TransferPhase.FetchingMetadata)

            {

                _logger.LogWarning("Metadata download timed out for {Name} after {Timeout}",

                    managed.Name, timeout);

                managed.SetError("Metadata download timed out — no peers found");

                SetHealth(managed, "Metadata download timed out — no peers found");

            }

        }

        catch (OperationCanceledException) { }

    }

    private void OnCoordinatorMetadataReceived(ManagedTorrent managed, byte[] metadata)

    {

        _logger.LogDebug("Metadata coordinator received metadata for {InfoHash}", managed.InfoHash);

        lock (_orch.MetadataCoordinatorLock)

        {

            if (_orch.MetadataCoordinators.TryGetValue(managed.InfoHash, out var coordinator))

            {

                coordinator.Dispose();

                _orch.MetadataCoordinators.Remove(managed.InfoHash);

            }

        }

        if (managed.SetMetadata(metadata))

        {

            _logger.LogDebug("Metadata successfully applied to torrent {InfoHash}", managed.InfoHash);

        }

        else

        {

            _logger.LogError("Failed to apply metadata to torrent {InfoHash}", managed.InfoHash);

            managed.SetError("Failed to parse downloaded metadata");

            SetHealth(managed, "Failed to parse downloaded metadata");

        }

    }

    private async Task AnnounceToTrackersForMetadataAsync(

        ManagedTorrent managed,

        MetadataDownloadCoordinator coordinator,

        CancellationToken cancellationToken)

    {

        if (managed.MagnetLinkData?.Trackers == null || managed.MagnetLinkData.Trackers.Count == 0)

        {

            _logger.LogDebug("No trackers in magnet link, relying on DHT");

            return;

        }

        _logger.LogDebug("Announcing to {Count} trackers for metadata download", managed.MagnetLinkData.Trackers.Count);

        foreach (var trackerUrl in managed.MagnetLinkData.Trackers)

        {

            try

            {

                _ = Task.Run(async () =>

                {

                    try

                    {

                        var peers = await AnnounceToTrackerAsync(managed, trackerUrl, cancellationToken).ConfigureAwait(false);

                        if (peers.Any())

                        {

                            _logger.LogDebug("Tracker {Url} returned {Count} peers", trackerUrl, peers.Count());

                            coordinator.AddPeers(peers);

                        }

                    }

                    catch (Exception ex)

                    {

                        _logger.LogDebug(ex, "Failed to announce to tracker {Url}", trackerUrl);

                    }

                }, cancellationToken);

            }

            catch (Exception ex)

            {

                _logger.LogDebug(ex, "Error starting tracker announce to {Url}", trackerUrl);

            }

        }

    }

    private async Task<IEnumerable<PeerInfo>> AnnounceToTrackerAsync(

        ManagedTorrent managed,

        string trackerUrl,

        CancellationToken cancellationToken)

    {

        if (!trackerUrl.StartsWith("http://") && !trackerUrl.StartsWith("https://"))

        {

            _logger.LogDebug("Skipping non-HTTP tracker: {Url}", trackerUrl);

            return Enumerable.Empty<PeerInfo>();

        }

        try

        {

            using var httpClient = new System.Net.Http.HttpClient();

            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var infoHashEncoded = Uri.EscapeDataString(

                System.Text.Encoding.Latin1.GetString(managed.InfoHashBytes!));

            var peerId = GeneratePeerId();

            var peerIdEncoded = Uri.EscapeDataString(

                System.Text.Encoding.Latin1.GetString(peerId));

            // RUNTIME: use monitor for live connection settings, fallback to persistence
            var listenPort = (_orch.ConnectionMonitorInternal?.CurrentValue ?? _orch.Persistence.Settings.Connection).ListenPort;

            var announceUrl = $"{trackerUrl}?info_hash={infoHashEncoded}&peer_id={peerIdEncoded}" +

                $"&port={listenPort}&uploaded=0&downloaded=0&left=0" +

                $"&compact=1&event=started&numwant=50";

            var response = await httpClient.GetByteArrayAsync(announceUrl, cancellationToken).ConfigureAwait(false);

            var parser = new BencodeParser();

            var obj = parser.Parse(response, out _);

            if (obj is BDictionary dict && dict.TryGetValue("peers", out var peersObj))

            {

                var peers = new List<PeerInfo>();

                if (peersObj is BString peersString)

                {

                    var data = peersString.Value.ToArray();

                    var parsedPeers = PeerInfo.FromCompactPeerList(data, source: "tracker");

                    if (parsedPeers != null)

                    {

                        peers.AddRange(parsedPeers);

                    }

                }

                return peers;

            }

        }

        catch (Exception ex)

        {

            _logger.LogDebug(ex, "Tracker announce failed: {Url}", trackerUrl);

        }

        return Enumerable.Empty<PeerInfo>();

    }

    private static byte[] GeneratePeerId()

    {

        string peerIdStr = ClientFingerprint.GeneratePeerIdFromPrefix(
            PeerCommunication.Configuration.ClientInfo.PeerIdPrefix);

        return System.Text.Encoding.ASCII.GetBytes(peerIdStr);

    }

    private void OnMagnetMetadataReceived(ManagedTorrent managed)
    {
        _ = OnMagnetMetadataReceivedAsync(managed);
    }

    private async Task OnMagnetMetadataReceivedAsync(ManagedTorrent managed)

    {

        if (managed?.Torrent == null)

            return;

        _logger.LogInformation("Metadata received for magnet link: {Name}", managed.Name);

        try

        {

            var torrentsDir = Path.Combine(_orch.Persistence.DataDirectory, "torrents");

            Directory.CreateDirectory(torrentsDir);

            var torrentPath = Path.Combine(torrentsDir, $"{managed.InfoHash}.torrent");

            var torrentBytes = managed.Torrent.ToBDictionary().EncodeAsBytes();

            await File.WriteAllBytesAsync(torrentPath, torrentBytes).ConfigureAwait(false);

            // Cache in resume data for fast startup (skip separate .torrent read on next boot)
            if (torrentBytes.Length <= TorrentResumeData.MaxEmbedTorrentFileSize)
                managed.ResumeData.TorrentFileBytes = torrentBytes;

            managed.TorrentFilePath = torrentPath;

            managed.ResumeData.TorrentFilePath = torrentPath;

            _logger.LogDebug("Saved torrent file to: {Path}", torrentPath);

            await _orch.Persistence.UpdateTorrentMetadataAsync(

                managed.InfoHash,

                managed.Torrent.Info.Name,

                managed.Torrent.TotalSize,

                managed.Torrent.PieceCount,

                (int)managed.Torrent.Info.PieceLength,

                managed.Torrent.Info.Files.Count,

                torrentPath).ConfigureAwait(false);

            var files = managed.Torrent.Info.Files.Select((f, index) => new FileRecord

            {

                InfoHash = managed.InfoHash,

                FileIndex = index,

                Path = string.Join("/", f.Path),

                Size = f.Length,

                Priority = 4

            });

            await _orch.Persistence.SaveFilesAsync(managed.InfoHash, files).ConfigureAwait(false);

            await _orch.Persistence.SaveResumeDataAsync(managed.InfoHash, managed.ResumeData).ConfigureAwait(false);

            if (!managed.UserPaused)

            {

                SetIntent(managed, UserIntent.Queued);

                _orch.AutoManager.Trigger();

            }

            else

            {

                SetIntent(managed, UserIntent.Paused);

            }

            var newStatus = managed.GetStatus();

            _orch.RaiseTorrentStatusChanged(

                managed.InfoHash,

                managed.Name,

                new TorrentStatus { Phase = TransferPhase.FetchingMetadata, Intent = UserIntent.Active },

                newStatus);

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Error processing received metadata for {InfoHash}", managed.InfoHash);

            managed.SetError($"Metadata processing failed: {ex.Message}");

            SetHealth(managed, $"Metadata processing failed: {ex.Message}");

        }

    }

    #endregion

    #region Remove Torrent

    public async Task<DeleteTorrentFilesResult?> RemoveTorrentAsync(

        string infoHash, bool deleteFiles = false,

        bool secureWipe = false, bool wipeMetadata = false,

        IProgress<DeletionProgress>? progress = null,

        CancellationToken cancellationToken = default)

    {

        var managed = _orch.TorrentsInternal.Find(infoHash);

        if (managed == null)

        {

            _logger.LogWarning("Torrent {InfoHash} not found for removal", infoHash);

            return null;

        }

        _logger.LogInformation(
            "Removing torrent: {Name} (deleteFiles: {DeleteFiles}, secureWipe: {SecureWipe}, wipeMetadata: {WipeMetadata})",
            managed.Name, deleteFiles, secureWipe, wipeMetadata);

        if (managed.Engine != null)

        {

            _logger.LogDebug("Stopping engine for removal: {Name}", managed.Name);

            await _orch.StopEngineAsync(managed, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Engine stopped for: {Name}", managed.Name);

        }

        // Unsubscribe event handler (subscribed for magnet links at add time)

        managed.MetadataReceived -= OnMagnetMetadataReceived;

        // Stop and remove metadata coordinator if active (magnet link still downloading metadata)

        lock (_orch.MetadataCoordinatorLock)

        {

            if (_orch.MetadataCoordinators.TryGetValue(infoHash, out var metaCoordinator))

            {

                _orch.MetadataCoordinators.Remove(infoHash);

                metaCoordinator.Dispose();

            }

        }

        _orch.StateIndex.Remove(managed);

        _orch.QueueManager.Remove(managed);

        _orch.TorrentsInternal.Remove(infoHash);

        _orch.PendingDhtPeers.TryRemove(infoHash, out _);

        // Unregister from DHT so we stop announcing this info hash

        _orch.DhtCoordinator.UnregisterTorrentFromDht(managed);

        _logger.LogDebug("Deleting persistence data for: {Name} (wipeMetadata: {WipeMetadata})",
            managed.Name, wipeMetadata);

        await _orch.Persistence.DeleteTorrentAsync(infoHash, wipeMetadata).ConfigureAwait(false);

        _logger.LogDebug("Persistence data deleted for: {Name}", managed.Name);

        DeleteTorrentFilesResult? result = null;

        if (deleteFiles && !string.IsNullOrEmpty(managed.SavePath))

        {

            try

            {

                _logger.LogDebug("Deleting content files for: {Name} (secureWipe: {SecureWipe})",
                    managed.Name, secureWipe);

                result = await DeleteTorrentFilesOnlyAsync(managed, secureWipe, progress, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Deleted torrent files for: {Name} (extraFiles: {HasExtra})",

                    managed.Name, result.HasExtraFiles);

            }

            catch (Exception ex)

            {

                _logger.LogWarning(ex, "Failed to delete files for torrent: {Name}", managed.Name);

            }

        }

        _orch.RaiseTorrentRemoved(infoHash, managed.Name, deleteFiles);

        _orch.AutoManager.Trigger();

        _logger.LogInformation("Torrent removed: {Name}", managed.Name);

        return result;

    }

    private async Task<DeleteTorrentFilesResult> DeleteTorrentFilesOnlyAsync(
        ManagedTorrent managed, bool secureWipe,
        IProgress<DeletionProgress>? progress = null,
        CancellationToken cancellationToken = default)

    {

        if (managed.Torrent != null)

        {

            // Delete each torrent-owned file

            // For multi-file torrents, file.Path is relative to info.Name (per BitTorrent spec),

            // so we must include the torrent name directory. For single-file torrents,

            // Path already contains [name] and the file lives directly in SavePath.

            var isMultiFile = managed.Torrent.Info.FileMode == Bencode.Torrents.TorrentFileMode.Multi;

            if (secureWipe && !_ssdWarningShown)

            {

                // Determine the path of the first content file to check its drive type

                string? firstFilePath = null;

                var firstFile = managed.Torrent.Info.Files.FirstOrDefault();

                if (firstFile != null)

                {

                    firstFilePath = isMultiFile

                        ? Path.Combine(managed.SavePath, managed.Torrent.Info.Name, Path.Combine(firstFile.Path.ToArray()))

                        : Path.Combine(managed.SavePath, Path.Combine(firstFile.Path.ToArray()));

                }

                firstFilePath ??= managed.SavePath;

                if (StorageDeviceHelper.IsSolidStateDrive(firstFilePath, _logger))

                {

                    _ssdWarningShown = true;

                    _logger.LogInformation(

                        "Secure deletion target is on SSD — overwriting is best-effort. " +

                        "Due to SSD wear leveling and over-provisioning, deleted data may persist in remapped flash cells. " +

                        "For guaranteed data destruction on SSDs, use full-disk encryption.");

                }

            }

            int fileIndex = 0;
            int totalFiles = managed.Torrent.Info.Files.Count;

            foreach (var file in managed.Torrent.Info.Files)

            {

                cancellationToken.ThrowIfCancellationRequested();

                var filePath = isMultiFile

                    ? Path.Combine(managed.SavePath, managed.Torrent.Info.Name, Path.Combine(file.Path.ToArray()))

                    : Path.Combine(managed.SavePath, Path.Combine(file.Path.ToArray()));

                progress?.Report(new DeletionProgress(
                    secureWipe ? DeletionPhase.SecureWiping : DeletionPhase.DeletingFiles,
                    filePath, fileIndex, totalFiles, 0, 0));

                if (File.Exists(filePath))

                {

                    if (secureWipe)

                    {

                        await _secureFileWiper.WipeFileAsync(filePath).ConfigureAwait(false);

                    }

                    else

                    {

                        await _deletionWorker.DeleteFileAsync(filePath).ConfigureAwait(false);

                    }

                    _logger.LogDebug("Deleted torrent file: {Path}", filePath);

                }

                fileIndex++;

            }

            // Delete partfile if it exists
            var partFilePath = Path.Combine(managed.SavePath,
                $".{managed.Torrent.GetInfoHashHex()}.parts");
            if (File.Exists(partFilePath))
            {
                await _deletionWorker.DeleteFileAsync(partFilePath).ConfigureAwait(false);
                _logger.LogDebug("Deleted partfile: {Path}", partFilePath);
            }

            var torrentDir = Path.Combine(managed.SavePath, managed.Torrent.Info.Name);

            // Single-file torrent or directory doesn't exist — nothing more to scan

            if (!Directory.Exists(torrentDir))

            {

                return new DeleteTorrentFilesResult

                {

                    HasExtraFiles = false,

                    TorrentDirectory = null

                };

            }

            // Clean up empty subdirectories bottom-up

            await _deletionWorker.CleanEmptyDirectoriesAsync(torrentDir).ConfigureAwait(false);

            // Check if directory still exists (may have been fully cleaned)

            if (!Directory.Exists(torrentDir))

            {

                return new DeleteTorrentFilesResult

                {

                    HasExtraFiles = false,

                    TorrentDirectory = null

                };

            }

            // Scan for remaining non-torrent files

            var remainingFiles = Directory

                .EnumerateFiles(torrentDir, "*", SearchOption.AllDirectories)

                .Select(f => Path.GetRelativePath(torrentDir, f))

                .ToList();

            if (remainingFiles.Count == 0)

            {

                // Only empty dirs remain — delete the root

                await _deletionWorker.DeleteDirectoryAsync(torrentDir, recursive: true).ConfigureAwait(false);

                return new DeleteTorrentFilesResult

                {

                    HasExtraFiles = false,

                    TorrentDirectory = null

                };

            }

            return new DeleteTorrentFilesResult

            {

                HasExtraFiles = true,

                ExtraFiles = remainingFiles,

                TorrentDirectory = torrentDir,

                SavePath = managed.SavePath

            };

        }

        else

        {

            // No metadata — fallback to recursive delete (unchanged)

            var torrentPath = Path.Combine(managed.SavePath, managed.Name);

            if (Directory.Exists(torrentPath))

            {

                if (secureWipe)

                {

                    // Wipe all files in the directory before deleting

                    foreach (var fp in Directory.EnumerateFiles(torrentPath, "*", SearchOption.AllDirectories))

                    {

                        await _secureFileWiper.WipeFileAsync(fp).ConfigureAwait(false);

                    }

                    await _deletionWorker.DeleteDirectoryAsync(torrentPath, recursive: true).ConfigureAwait(false);

                }

                else

                {

                    await _deletionWorker.DeleteDirectoryAsync(torrentPath, recursive: true).ConfigureAwait(false);

                }

            }

            else if (File.Exists(torrentPath))

            {

                if (secureWipe)

                {

                    await _secureFileWiper.WipeFileAsync(torrentPath).ConfigureAwait(false);

                }

                else

                {

                    await _deletionWorker.DeleteFileAsync(torrentPath).ConfigureAwait(false);

                }

            }

            else

            {

                foreach (var ext in new[] { "", ".mkv", ".mp4", ".avi", ".zip", ".rar", ".iso" })

                {

                    var possiblePath = torrentPath + ext;

                    if (File.Exists(possiblePath))

                    {

                        if (secureWipe)

                        {

                            await _secureFileWiper.WipeFileAsync(possiblePath).ConfigureAwait(false);

                        }

                        else

                        {

                            await _deletionWorker.DeleteFileAsync(possiblePath).ConfigureAwait(false);

                        }

                        break;

                    }

                }

            }

            return new DeleteTorrentFilesResult

            {

                HasExtraFiles = false,

                TorrentDirectory = null

            };

        }

    }

    public async Task DeleteRemainingFilesAsync(string torrentDirectory, string savePath)

    {

        if (string.IsNullOrEmpty(torrentDirectory))

            return;

        // Guard: only allow deletion under the torrent's actual save path

        var normalizedDir = Path.GetFullPath(torrentDirectory);

        var normalizedSave = Path.GetFullPath(savePath);

        if (!normalizedDir.StartsWith(normalizedSave, StringComparison.OrdinalIgnoreCase))

        {

            _logger.LogWarning("Refused to delete directory outside save path: {Path}", torrentDirectory);

            return;

        }

        if (Directory.Exists(torrentDirectory))

        {

            await _deletionWorker.DeleteDirectoryAsync(normalizedDir, recursive: true).ConfigureAwait(false);

            _logger.LogInformation("Deleted remaining files in: {Path}", torrentDirectory);

        }

    }

    #endregion

    #region Start / Pause

    public async Task StartTorrentAsync(string infoHash, CancellationToken cancellationToken = default)

    {

        var managed = _orch.TorrentsInternal.Find(infoHash)

            ?? throw new KeyNotFoundException($"Torrent {infoHash} not found");

        // Already active — if forced, restore auto-managed and notify UI

        // so ForcedDownloading→Downloading (or ForcedSeeding→Seeding).

        if (IsActivelyTransferring(managed.GetStatus()))

        {

            if (!managed.IsAutoManaged)

            {

                var oldStatus = managed.GetStatus();

                managed.IsAutoManaged = true;

                NotifyStatusChanged(managed, oldStatus);

            }

            return;

        }

        _logger.LogInformation("Starting torrent: {Name}", managed.Name);

        // Restore auto-management so previously-forced torrents re-enter the queue

        managed.IsAutoManaged = true;

        managed.UserPaused = false;

        managed.ResumeData.UserPaused = false;

        // Recover from Error state: destroy the broken engine and restart fresh

        if (managed.GetStatus().Error.HasValue && managed.Engine != null)

        {

            _logger.LogInformation("Recovering from error state for: {Name}", managed.Name);

            managed.ClearError();

            await _orch.StopEngineAsync(managed, cancellationToken).ConfigureAwait(false);

        }

        if (managed.Engine != null && managed.Engine.IsPaused)

        {

            _logger.LogInformation("Resuming paused engine for: {Name}", managed.Name);

            await managed.Engine.ResumeAsync(cancellationToken).ConfigureAwait(false);

            var oldStatus = managed.GetStatus();

            // ResumeAsync posts IntentTrigger.Activate itself — posting again here
            // is rejected by the intent machine (no Active→Active) and logs a
            // spurious warning on every resume.

            managed.LastActiveTime = DateTime.UtcNow;

            NotifyStatusChanged(managed, oldStatus);

            // Save resume data so crash-after-resume doesn't revert to paused
            if ((_orch.AutoSaveMonitorInternal?.CurrentValue ?? _orch.Persistence.Settings.AutoSave).SaveOnResume)
            {
                _orch.UpdateResumeDataFromTorrent(managed);
                await _orch.Persistence.SaveResumeDataAsync(managed.InfoHash, managed.ResumeData).ConfigureAwait(false);
            }

            return;

        }

        {

            var oldStatus = managed.GetStatus();

            if (managed.IsAutoManaged)

            {

                SetIntent(managed, UserIntent.Queued);

            }

            else

            {

                _orch.StartTorrentInternal(managed);

                SetIntent(managed, UserIntent.Active);

            }

            NotifyStatusChanged(managed, oldStatus);

        }

    }

    /// <summary>

    /// Force start a torrent, bypassing auto-management queue limits.

    /// Sets IsAutoManaged=false and starts immediately regardless of slot limits.

    /// </summary>

    public async Task ForceStartAsync(string infoHash, CancellationToken cancellationToken = default)

    {

        var managed = _orch.TorrentsInternal.Find(infoHash)

            ?? throw new KeyNotFoundException($"Torrent {infoHash} not found");

        _logger.LogInformation("Force resuming torrent: {Name}", managed.Name);

        // Bypass auto-management: set manual mode + clear user pause

        managed.IsAutoManaged = false;

        managed.UserPaused = false;

        managed.ResumeData.UserPaused = false;

        // Already active — just notify the UI that the auto-managed flag changed

        // so the display state flips from Downloading→ForcedDownloading (or Seeding→ForcedSeeding).

        if (IsActivelyTransferring(managed.GetStatus()))

        {

            NotifyStatusChanged(managed, managed.GetStatus());

            return;

        }

        // Recover from Error state: destroy the broken engine and restart fresh

        if (managed.GetStatus().Error.HasValue && managed.Engine != null)

        {

            _logger.LogInformation("Recovering from error state for force resume: {Name}", managed.Name);

            managed.ClearError();

            await _orch.StopEngineAsync(managed, cancellationToken).ConfigureAwait(false);

        }

        {

            var oldStatus = managed.GetStatus();


            if (managed.Engine != null && managed.Engine.IsPaused)

            {

                await managed.Engine.ResumeAsync(cancellationToken).ConfigureAwait(false);

                // ResumeAsync posts IntentTrigger.Activate itself — posting again here
                // is rejected by the intent machine (no Active→Active) and logs a
                // spurious warning on every resume.

                managed.LastActiveTime = DateTime.UtcNow;

                // Save resume data so crash-after-force-resume doesn't revert to paused
                if ((_orch.AutoSaveMonitorInternal?.CurrentValue ?? _orch.Persistence.Settings.AutoSave).SaveOnResume)
                {
                    _orch.UpdateResumeDataFromTorrent(managed);
                    await _orch.Persistence.SaveResumeDataAsync(managed.InfoHash, managed.ResumeData).ConfigureAwait(false);
                }

            }

            else

            {

                // Start immediately regardless of queue slot availability

                _orch.StartTorrentInternal(managed);

                SetIntent(managed, UserIntent.Active);

            }

            NotifyStatusChanged(managed, oldStatus);

        }

    }

    public async Task PauseTorrentAsync(string infoHash, CancellationToken cancellationToken = default)

    {

        var managed = _orch.TorrentsInternal.Find(infoHash)

            ?? throw new KeyNotFoundException($"Torrent {infoHash} not found");

        if (managed.GetStatus().Intent == UserIntent.Paused || managed.GetStatus().Phase == TransferPhase.Idle)

        {

            _logger.LogDebug("Torrent {InfoHash} is already paused/stopped", infoHash);

            return;

        }

        _logger.LogInformation("Pausing torrent: {Name}", managed.Name);

        managed.UserPaused = true;

        managed.ResumeData.UserPaused = true;

        if (managed.Engine != null)

        {

            managed.Statistics.DownloadRate = 0;

            managed.Statistics.UploadRate = 0;

            await managed.Engine.PauseAsync().ConfigureAwait(false);

        }

        var oldStatus = managed.GetStatus();


        SetIntent(managed, UserIntent.Paused);

        NotifyStatusChanged(managed, oldStatus);

        if ((_orch.AutoSaveMonitorInternal?.CurrentValue ?? _orch.Persistence.Settings.AutoSave).SaveOnPause)

        {

            _orch.UpdateResumeDataFromTorrent(managed);

            await _orch.Persistence.SaveResumeDataAsync(managed.InfoHash, managed.ResumeData).ConfigureAwait(false);

        }

        _orch.AutoManager.Trigger();

    }

    #endregion

    #region Force Recheck

    public async Task ForceRecheckAsync(string infoHash, bool resume = false, CancellationToken cancellationToken = default)

    {

        var managed = _orch.TorrentsInternal.Find(infoHash)

            ?? throw new KeyNotFoundException($"Torrent {infoHash} not found");

        _logger.LogInformation("Force rechecking torrent: {Name} (resume={Resume})", managed.Name, resume);

        // Preserve file priorities from the running engine before destroying it.

        // libtorrent's force_recheck() clears piece state but keeps file priorities intact.

        if (managed.Engine != null)

        {

            var currentPriorities = managed.Engine.GetAllFilePriorities();

            if (currentPriorities.Count > 0 && currentPriorities.Any(p => p != FilePriority.Normal))

            {

                managed.PendingFilePriorities = currentPriorities.ToArray();

            }

            await _orch.StopEngineAsync(managed, cancellationToken).ConfigureAwait(false);

        }

        // Determine start piece for resume mode

        int startPiece = 0;

        if (resume && managed.ResumeData.CheckingCheckpoint.HasValue)

        {

            startPiece = managed.ResumeData.CheckingCheckpoint.Value + 1;

            _logger.LogInformation("Resuming recheck from piece {StartPiece} (checkpoint {Checkpoint})",

                startPiece, managed.ResumeData.CheckingCheckpoint.Value);

        }

        else

        {

            // Full recheck — clear piece state (file priorities are preserved, libtorrent model)

            managed.ResumeData.HavePieces = null;

            managed.ResumeData.VerifiedPieces = null;

            managed.ResumeData.UnfinishedPieces = null;

            managed.ResumeData.CheckingCheckpoint = null;

            managed.Statistics.PiecesCompleted = 0;

            managed.Statistics.TotalDone = 0;

            managed.IsFinished = false;

        }

        {

            var oldStatus = managed.GetStatus();


            // Clear error so UI shows Verifying, not Error (Bug 3 fix).
            // If recheck itself fails (no metadata), error is re-set at the metadata check below.
            managed.StateController.PostClearError();

            managed.StateController.PostPhase(PhaseTrigger.CheckFiles);

            managed.StateController.PostFileOp(FileOpTrigger.StartRecheck);

            NotifyStatusChanged(managed, oldStatus);

        }

        if (managed.Torrent == null)

        {

            _logger.LogWarning("Cannot verify torrent without metadata: {InfoHash}", infoHash);

            var oldStatus = managed.GetStatus();


            SetHealth(managed, "Cannot verify: torrent metadata not available");

            NotifyStatusChanged(managed, oldStatus);

            return;

        }

        // Analyze files: compute skip set for missing files & detect oversized files

        var expectedFiles = GetExpectedFilePaths(managed);

        var (skipPieces, unverifiablePieces, oversizedFiles) = AnalyzeFilesForRecheck(managed, expectedFiles);

        if (oversizedFiles.Count > 0)

        {

            _orch.Alerts.Post(new OversizedFilesAlert(infoHash, oversizedFiles));

        }

        var verificationResult = await VerifyAllPiecesAsync(managed, startPiece, skipPieces, cancellationToken).ConfigureAwait(false);

        // Build the verified bitfield — for resume mode, merge with existing bitfield

        var verifiedBitfield = new BitArray(managed.Torrent.PieceCount, false);

        if (resume && managed.ResumeData.HavePieces != null)

        {

            // Copy existing verified state from prior checkpoint run

            var existing = managed.ResumeData.GetHavePiecesBitArray();

            if (existing != null)

            {

                for (int i = 0; i < Math.Min(startPiece, managed.Torrent.PieceCount); i++)

                {

                    verifiedBitfield[i] = existing[i];

                }

            }

        }

        foreach (var pieceIndex in verificationResult.VerifiedPieces)

        {

            verifiedBitfield[pieceIndex] = true;

        }

        managed.ResumeData.SetHavePieces(verifiedBitfield);

        managed.ResumeData.VerifiedPieces = managed.ResumeData.HavePieces;

        // Clear checkpoint on successful completion

        managed.ResumeData.CheckingCheckpoint = null;

        int totalVerified = 0;

        for (int i = 0; i < managed.Torrent.PieceCount; i++)

        {

            if (verifiedBitfield[i]) totalVerified++;

        }

        managed.Statistics.PiecesCompleted = totalVerified;

        managed.Statistics.TotalDone = CalculateTotalDone(managed, totalVerified);

        // Check completion based on wanted pieces (respects file priorities)

        bool isComplete;

        if (managed.PendingFilePriorities != null && managed.PendingFilePriorities.Any(p => p == FilePriority.Skip))

        {

            // Build a temporary tracker to determine which pieces are wanted

            var tempTracker = new FileProgressTracker(managed.Torrent.Info);

            tempTracker.SetFilePriorities(managed.PendingFilePriorities);

            isComplete = true;

            for (int i = 0; i < managed.Torrent.PieceCount; i++)

            {

                if (tempTracker.IsPieceWanted(i) && !verifiedBitfield[i] && !unverifiablePieces.Contains(i))

                {

                    isComplete = false;

                    break;

                }

            }

        }

        else

        {

            isComplete = totalVerified == managed.Torrent.PieceCount;

        }

        managed.IsFinished = isComplete;

        if (isComplete)

        {

            managed.CompletedTime = DateTime.UtcNow;

            managed.Statistics.CompletedTime = managed.CompletedTime;

            _logger.LogInformation("Force recheck complete: {Name} is 100% verified", managed.Name);

        }

        else

        {

            _logger.LogInformation("Force recheck complete: {Name} has {Verified}/{Total} pieces ({Corrupt} corrupt, {Missing} missing, {Inconsistent} inconsistent)",

                managed.Name,

                totalVerified,

                verificationResult.TotalPieces,

                verificationResult.CorruptPieces.Count,

                verificationResult.MissingPieces.Count,

                verificationResult.InconsistentPieces.Count);

            // Provide actionable diagnostics when pieces are missing

            var missingFileCount = expectedFiles.Count(f => !File.Exists(f.path));

            if (verificationResult.MissingPieces.Count > 0 && missingFileCount > 0)

            {

                _logger.LogWarning("All {Missing} missing pieces are likely caused by {FileCount} file(s) not found at save path '{SavePath}'",

                    verificationResult.MissingPieces.Count, missingFileCount, managed.SavePath);

                _logger.LogWarning("To fix: move the files to '{SavePath}' or change the torrent's save path to where the files are located",

                    managed.SavePath);

            }

        }

        _orch.Alerts.Post(new CheckingCompleteAlert(

            infoHash,

            totalVerified,

            verificationResult.CorruptPieces.Count,

            verificationResult.TotalPieces,

            verificationResult.InconsistentPieces.Count));

        // Tell engine to trust the bitfield on next start — we just verified every piece.
        // Cleared in StartEngineAsync after fast resume consumes it.
        // CRITICAL: Set BEFORE NotifyStatusChanged, which triggers AutoManager.Trigger()
        // synchronously. Without this ordering, the auto-manager can start the engine
        // before the NoVerifyFiles flag is set, causing it to take an unreliable
        // fast-resume path that may fail and briefly enter Downloading state.
        managed.ResumeData.Flags |= TorrentFlags.NoVerifyFiles;

        // Persist resume data BEFORE triggering auto-manager so the engine sees
        // consistent state (HavePieces + NoVerifyFiles + LastSaved) on startup.
        await _orch.Persistence.SaveResumeDataAsync(infoHash, managed.ResumeData).ConfigureAwait(false);

        _logger.LogWarning("[DIAG] ForceRecheckAsync END: IsFinished={IsFinished}, flags={Flags}, NoVerifyFiles={NoVerify}, " +
            "HavePieces null={HavePiecesNull}, HavePieces.Length={HavePiecesLen}, " +
            "ResumeData.PieceCount={RdPieceCount}, Torrent.PieceCount={TorrentPieceCount}, " +
            "totalVerified={Verified}",
            managed.IsFinished, managed.ResumeData.Flags,
            managed.ResumeData.Flags.HasFlag(TorrentFlags.NoVerifyFiles),
            managed.ResumeData.HavePieces == null,
            managed.ResumeData.HavePieces?.Length ?? -1,
            managed.ResumeData.PieceCount,
            managed.Torrent!.PieceCount,
            totalVerified);

        {

            // Clear error state after successful recheck — verification passed, health is OK

            managed.ClearError();

            var oldStatus = managed.GetStatus();


            SetIntent(managed, UserIntent.Queued);

            // Clear recheck file op now that checking is done

            managed.StateController.PostFileOp(FileOpTrigger.Finish);

            managed.StateController.PostMetrics(fileOpProgress: 0);

            // This triggers AutoManager.Trigger() → Recalculate() → StartTorrentInternal.
            // NoVerifyFiles flag and resume data are already saved above.
            NotifyStatusChanged(managed, oldStatus);

        }

    }

    private async Task<VerificationResult> VerifyAllPiecesAsync(

        ManagedTorrent managed,

        int startPiece,

        HashSet<int> skipPieces,

        CancellationToken cancellationToken)

    {

        var result = new VerificationResult

        {

            TotalPieces = managed.Torrent!.PieceCount,

            StartTime = DateTime.UtcNow

        };

        var lockManager = new FileLockManager();

        var sparseFileManager = new SparseFileManager(managed.SavePath, managed.Torrent.Info);

        // RUNTIME: use monitor for live settings, fallback to persistence

        var diskSettings = _orch.DiskMonitorInternal?.CurrentValue ?? _orch.Persistence.Settings.Disk;

        var backendLogger = _orch.LoggerFactoryInternal.CreateLogger("DiskBackend");

        await using var diskBackend = DiskBackendFactory.Create(

            diskSettings,

            perTorrentOverride: null,

            perTorrentWriteMode: null,

            sparseFileManager,

            lockManager,

            backendLogger,

            _orch.DiskMonitorInternal,

            accessHint: DiskAccessHint.CheckingMode);

        var mapper = new PieceMapper(managed.SavePath, managed.Torrent.Info);

        var verifier = new PieceVerifier(managed.Torrent.Info);

        var hashThreads = Math.Max(1, diskSettings.HashThreads);

        var pipeline = new PieceVerificationPipeline(

            diskBackend,

            verifier,

            mapper,

            managed.Torrent.PieceCount,

            diskSettings.CheckingMemUsage,

            hashThreads);

        var verifiedPieces = new ConcurrentBag<int>();

        var corruptPieces = new ConcurrentBag<int>();

        var missingPieces = new ConcurrentBag<int>();

        var inconsistentPieces = new ConcurrentBag<int>();

        int piecesChecked = 0;

        long lastProgressUpdateTicks = 0;

        // DirectProgress runs the callback on the calling (hasher) thread directly,
        // unlike Progress<T> which captures SynchronizationContext at construction
        // and marshals every callback to it. If constructed on the UI thread
        // (engine was null → no prior yield in ForceRecheckAsync), Progress<T>
        // would flood the Avalonia dispatcher with thousands of per-piece callbacks.
        var progress = new DirectProgress<PieceVerificationPipeline.VerificationProgress>(p =>

        {

            // Categorize results based on detailed PieceVerifyResult

            switch (p.Result)

            {

                case PieceVerifyResult.Valid:

                    verifiedPieces.Add(p.PieceIndex);

                    break;

                case PieceVerifyResult.Inconsistent:

                    inconsistentPieces.Add(p.PieceIndex);

                    // Inconsistent pieces still pass V1 — treat as valid for bitfield

                    verifiedPieces.Add(p.PieceIndex);

                    break;

                default:

                    // CorruptV1, CorruptV2 — piece is corrupt

                    corruptPieces.Add(p.PieceIndex);

                    break;

            }

            var checked_ = System.Threading.Interlocked.Increment(ref piecesChecked);

            // Write checkpoint every 64 pieces for resume support

            if (checked_ % 64 == 0)

            {

                managed.ResumeData.CheckingCheckpoint = p.PieceIndex;

            }

            // Throttle progress updates to max ~10/sec (every 100ms).
            // Uses single-lock UpdateFileOpProgress instead of double-lock
            // GetStatus() + UpdateStatus() pattern.
            var now = Environment.TickCount64;

            var last = System.Threading.Interlocked.Read(ref lastProgressUpdateTicks);

            bool isLast = checked_ >= managed.Torrent!.PieceCount;

            if (isLast || now - last >= 100)

            {

                if (isLast || Interlocked.CompareExchange(ref lastProgressUpdateTicks, now, last) == last)

                {

                    var progressValue = (double)checked_ / managed.Torrent.PieceCount;

                    managed.UpdateFileOpProgress(progressValue);

                }

            }

        });

        try

        {

            var bitfield = await pipeline.VerifyAllPiecesAsync(

                progress,

                startPiece,

                skipPieces.Count > 0 ? skipPieces : null,

                cancellationToken).ConfigureAwait(false);

            // Any pieces not in verifiedPieces, corruptPieces, inconsistentPieces, or skipPieces are missing

            var knownPieces = new HashSet<int>(verifiedPieces);

            foreach (var p in corruptPieces) knownPieces.Add(p);

            for (int i = startPiece; i < managed.Torrent.PieceCount; i++)

            {

                if (!knownPieces.Contains(i) && !skipPieces.Contains(i))

                {

                    missingPieces.Add(i);

                }

            }

            result.VerifiedPieces = verifiedPieces.ToList();

            result.CorruptPieces = corruptPieces.ToList();

            result.MissingPieces = missingPieces.ToList();

            result.InconsistentPieces = inconsistentPieces.ToList();

            result.Success = result.CorruptPieces.Count == 0 && result.MissingPieces.Count == 0;

        }

        catch (OperationCanceledException)

        {

            _logger.LogInformation("Verification cancelled for {Name} at piece ~{Checked}", managed.Name, piecesChecked);

            // Save checkpoint on cancellation for resume

            managed.ResumeData.CheckingCheckpoint = piecesChecked > 0 ? startPiece + piecesChecked - 1 : startPiece;

            await _orch.Persistence.SaveResumeDataAsync(managed.InfoHash, managed.ResumeData).ConfigureAwait(false);

            result.Cancelled = true;

            // Still populate partial results

            result.VerifiedPieces = verifiedPieces.ToList();

            result.CorruptPieces = corruptPieces.ToList();

            result.MissingPieces = missingPieces.ToList();

            result.InconsistentPieces = inconsistentPieces.ToList();

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Error during verification of {Name}", managed.Name);

            result.Error = ex;

        }

        result.EndTime = DateTime.UtcNow;

        result.Duration = result.EndTime - result.StartTime;

        return result;

    }

    private (HashSet<int> skipPieces, HashSet<int> unverifiablePieces, List<(string Path, long Expected, long Actual)> oversizedFiles)

        AnalyzeFilesForRecheck(ManagedTorrent managed, List<(string path, long size)> expectedFiles)

    {

        var missingFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var oversizedFiles = new List<(string Path, long Expected, long Actual)>();

        foreach (var (path, expectedSize) in expectedFiles)

        {

            if (!File.Exists(path))

            {

                missingFilePaths.Add(path);

                _logger.LogWarning("  Missing: {Path}", path);

            }

            else

            {

                var actualSize = new FileInfo(path).Length;

                if (actualSize > expectedSize)

                {

                    oversizedFiles.Add((path, expectedSize, actualSize));

                    _logger.LogWarning("  Oversized: {Path} ({Actual} bytes, expected {Expected})",

                        path, actualSize, expectedSize);

                }

            }

        }

        var skipPieces = new HashSet<int>();
        var unverifiablePieces = new HashSet<int>();

        if (missingFilePaths.Count > 0)

        {

            var mapper = new PieceMapper(managed.SavePath, managed.Torrent!.Info);

            var partFileBackend = managed.Engine?.DiskBackendInternal as PartFileAwareDiskBackend;

            // Identify skipped file paths
            var skippedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (managed.PendingFilePriorities != null)
            {
                for (int fi = 0; fi < managed.PendingFilePriorities.Length; fi++)
                {
                    if (managed.PendingFilePriorities[fi] == FilePriority.Skip)
                    {
                        var filePath = expectedFiles.Count > fi ? expectedFiles[fi].path : null;
                        if (filePath != null) skippedFilePaths.Add(filePath);
                    }
                }
            }

            for (int i = 0; i < managed.Torrent.PieceCount; i++)

            {

                var location = mapper.MapPieceToFiles(i);

                bool allSegmentsMissing = location.FileSegments.All(s => missingFilePaths.Contains(s.FilePath));
                if (allSegmentsMissing)
                {
                    skipPieces.Add(i);
                    continue;
                }

                // Shared piece: some segments missing AND in skipped files
                bool hasSkippedMissingSegment = location.FileSegments.Any(s =>
                    missingFilePaths.Contains(s.FilePath) && skippedFilePaths.Contains(s.FilePath));

                if (hasSkippedMissingSegment)
                {
                    if (partFileBackend != null && partFileBackend.HasPieceInPartFile(i))
                    {
                        // Partfile has data — let verification proceed
                    }
                    else
                    {
                        unverifiablePieces.Add(i);
                        skipPieces.Add(i);
                    }
                }

            }

            _logger.LogInformation(
                "Skipping {SkipCount} pieces (entirely missing), {UnverifiableCount} unverifiable (shared with skipped files)",
                skipPieces.Count - unverifiablePieces.Count, unverifiablePieces.Count);

        }

        return (skipPieces, unverifiablePieces, oversizedFiles);

    }

    private static long CalculateTotalDone(ManagedTorrent managed, int verifiedPieceCount)

    {

        if (managed.Torrent == null || verifiedPieceCount == 0)

            return 0;

        long pieceLength = managed.Torrent.Info.PieceLength;

        int totalPieces = managed.Torrent.PieceCount;

        if (verifiedPieceCount == totalPieces)

        {

            return managed.Torrent.TotalSize;

        }

        return (long)verifiedPieceCount * pieceLength;

    }

    #endregion

    #region Change Save Path

    public async Task<bool> ChangeSavePathAsync(string infoHash, string newSavePath, CancellationToken cancellationToken = default)

    {

        var managed = _orch.TorrentsInternal.Find(infoHash)

            ?? throw new KeyNotFoundException($"Torrent {infoHash} not found");

        if (string.IsNullOrWhiteSpace(newSavePath))

            throw new ArgumentException("New save path cannot be empty", nameof(newSavePath));

        var oldSavePath = managed.SavePath;

        if (string.Equals(oldSavePath, newSavePath, StringComparison.OrdinalIgnoreCase))

        {

            _logger.LogDebug("Save path unchanged for {Name}", managed.Name);

            return true;

        }

        _logger.LogInformation("Changing save path for {Name}: {OldPath} -> {NewPath}",

            managed.Name, oldSavePath, newSavePath);

        var currentStatus = managed.GetStatus();

        var wasActive = currentStatus.Phase is TransferPhase.Downloading or TransferPhase.Seeding or TransferPhase.Connecting;

        try

        {

            if (managed.Engine != null)

            {

                _logger.LogDebug("Using fence-based move_storage for {Name} (preserving peers)", managed.Name);

                {

                    managed.StateController.PostFileOp(FileOpTrigger.StartMove);

                }

                var result = await managed.Engine.MoveStorageAsync(newSavePath, cancellationToken).ConfigureAwait(false);

                if (!result.IsSuccess)

                {

                    _logger.LogWarning("MoveStorageAsync failed for {Name}: {Error}. Torrent will continue from original location.",

                        managed.Name, result.ErrorMessage);

                    // Restore previous state via new path

                    managed.StateController.PostFileOp(FileOpTrigger.Finish);

                    return false;

                }

                managed.SavePath = newSavePath;

                managed.ResumeData.SavePath = newSavePath;

                await _orch.Persistence.Database.UpdateSavePathAsync(infoHash, newSavePath).ConfigureAwait(false);

                await _orch.Persistence.SaveResumeDataAsync(infoHash, managed.ResumeData).ConfigureAwait(false);

                _logger.LogInformation("Successfully moved torrent {Name} to {NewPath} (peers preserved)",

                    managed.Name, newSavePath);

                {

                    managed.StateController.PostFileOp(FileOpTrigger.Finish);

                    managed.StateController.PostIntent(IntentTrigger.Activate);

                }

                if (result.NeedsRecheck)

                {

                    _logger.LogDebug("Cross-volume move completed, scheduling recheck for {Name}", managed.Name);

                }

                return true;

            }

            {

                managed.StateController.PostFileOp(FileOpTrigger.StartMove);

            }

            Directory.CreateDirectory(newSavePath);

            var moveSuccess = await MoveFilesAsync(managed, oldSavePath, newSavePath, cancellationToken).ConfigureAwait(false);

            if (!moveSuccess)

            {

                _logger.LogError("Failed to move files for {Name}", managed.Name);

                var oldStatus2 = managed.GetStatus();


                SetHealth(managed, "Failed to move files to new location");

                NotifyStatusChanged(managed, oldStatus2);

                return false;

            }

            managed.SavePath = newSavePath;

            managed.ResumeData.SavePath = newSavePath;

            await _orch.Persistence.Database.UpdateSavePathAsync(infoHash, newSavePath).ConfigureAwait(false);

            await _orch.Persistence.SaveResumeDataAsync(infoHash, managed.ResumeData).ConfigureAwait(false);

            _logger.LogInformation("Successfully moved torrent {Name} to {NewPath}", managed.Name, newSavePath);

            {

                managed.StateController.PostFileOp(FileOpTrigger.Finish);

                if (wasActive && !managed.UserPaused)

                {

                    _logger.LogDebug("Resuming torrent after move: {Name}", managed.Name);

                    SetIntent(managed, UserIntent.Queued);

                }

                else

                {

                    SetIntent(managed, UserIntent.Paused);

                }

            }

            return true;

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Error changing save path for {Name}", managed.Name);

            var oldStatus = managed.GetStatus();


            SetHealth(managed, $"Failed to change location: {ex.Message}");

            NotifyStatusChanged(managed, oldStatus);

            return false;

        }

    }

    private async Task<bool> MoveFilesAsync(ManagedTorrent managed, string oldSavePath, string newSavePath, CancellationToken cancellationToken)

    {

        if (managed.Torrent == null)

        {

            _logger.LogWarning("Cannot move files: no torrent metadata for {InfoHash}", managed.InfoHash);

            return false;

        }

        var torrentName = managed.Torrent.Info.Name;

        var oldTorrentDir = Path.Combine(oldSavePath, torrentName);

        var newTorrentDir = Path.Combine(newSavePath, torrentName);

        if (!Directory.Exists(oldTorrentDir) && !File.Exists(oldTorrentDir))

        {

            _logger.LogDebug("No files to move for {Name}, just updating path", managed.Name);

            return true;

        }

        var movedFiles = new List<(string Source, string Dest)>();

        try

        {

            if (managed.Torrent.Info.Files.Count == 1 && !Directory.Exists(oldTorrentDir))

            {

                var singleFilePath = Path.Combine(oldSavePath, managed.Torrent.Info.Files[0].Path[0]);

                var newFilePath = Path.Combine(newSavePath, managed.Torrent.Info.Files[0].Path[0]);

                if (File.Exists(singleFilePath))

                {

                    await MoveOrCopyFileAsync(singleFilePath, newFilePath, movedFiles, cancellationToken).ConfigureAwait(false);

                }

            }

            else if (Directory.Exists(oldTorrentDir))

            {

                try

                {

                    Directory.Move(oldTorrentDir, newTorrentDir);

                    _logger.LogDebug("Moved entire directory for {Name}", managed.Name);

                    return true;

                }

                catch (IOException ex) when (IsCrossVolumeException(ex))

                {

                    _logger.LogDebug("Cross-volume move detected, copying files individually");

                    await CopyDirectoryAsync(oldTorrentDir, newTorrentDir, movedFiles, cancellationToken).ConfigureAwait(false);

                    Directory.Delete(oldTorrentDir, recursive: true);

                }

            }

            return true;

        }

        catch (Exception ex)

        {

            _logger.LogError(ex, "Error moving files for {Name}", managed.Name);

            _logger.LogDebug("Rolling back {Count} moved files", movedFiles.Count);

            foreach (var (source, dest) in movedFiles)

            {

                try

                {

                    if (File.Exists(dest))

                    {

                        var sourceDir = Path.GetDirectoryName(source);

                        if (!string.IsNullOrEmpty(sourceDir))

                            Directory.CreateDirectory(sourceDir);

                        File.Move(dest, source, overwrite: true);

                    }

                }

                catch (Exception rollbackEx)

                {

                    _logger.LogWarning(rollbackEx, "Failed to rollback file: {Dest}", dest);

                }

            }

            return false;

        }

    }

    private static async Task MoveOrCopyFileAsync(string source, string dest, List<(string, string)> movedFiles, CancellationToken cancellationToken)

    {

        var destDir = Path.GetDirectoryName(dest);

        if (!string.IsNullOrEmpty(destDir))

            Directory.CreateDirectory(destDir);

        try

        {

            File.Move(source, dest, overwrite: true);

            movedFiles.Add((source, dest));

        }

        catch (IOException ex) when (IsCrossVolumeException(ex))

        {

            await CopyFileWithProgressAsync(source, dest, cancellationToken).ConfigureAwait(false);

            movedFiles.Add((source, dest));

            File.Delete(source);

        }

    }

    private static async Task CopyDirectoryAsync(string sourceDir, string destDir, List<(string, string)> movedFiles, CancellationToken cancellationToken)

    {

        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))

        {

            cancellationToken.ThrowIfCancellationRequested();

            var destFile = Path.Combine(destDir, Path.GetFileName(file));

            await CopyFileWithProgressAsync(file, destFile, cancellationToken).ConfigureAwait(false);

            movedFiles.Add((file, destFile));

        }

        foreach (var dir in Directory.GetDirectories(sourceDir))

        {

            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));

            await CopyDirectoryAsync(dir, destSubDir, movedFiles, cancellationToken).ConfigureAwait(false);

        }

    }

    private static async Task CopyFileWithProgressAsync(string source, string dest, CancellationToken cancellationToken)

    {

        const int bufferSize = 81920;

        using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);

        using var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

        await sourceStream.CopyToAsync(destStream, bufferSize, cancellationToken).ConfigureAwait(false);

    }

    private static bool IsCrossVolumeException(IOException ex)

    {

        return ex.HResult == unchecked((int)0x80070011) ||

               ex.Message.Contains("not on the same disk", StringComparison.OrdinalIgnoreCase) ||

               ex.Message.Contains("cross-device", StringComparison.OrdinalIgnoreCase);

    }

    #endregion

    #region Bulk Operations

    public async Task PauseAllAsync(CancellationToken cancellationToken = default)

    {

        _logger.LogInformation("Pausing all torrents");

        foreach (var torrent in _orch.TorrentsInternal.ToList())

        {

            if (torrent.GetStatus().Phase is TransferPhase.Downloading or TransferPhase.Seeding)

            {

                await PauseTorrentAsync(torrent.InfoHash, cancellationToken).ConfigureAwait(false);

            }

        }

    }

    public async Task ResumeAllAsync(CancellationToken cancellationToken = default)

    {

        _logger.LogInformation("Resuming all torrents");

        foreach (var torrent in _orch.TorrentsInternal.ToList())

        {

            if (torrent.GetStatus().Intent == UserIntent.Paused && torrent.UserPaused)

            {

                await StartTorrentAsync(torrent.InfoHash, cancellationToken).ConfigureAwait(false);

            }

        }

    }

    #endregion

    #region BEP 52 Merkle Trees

    /// <summary>

    /// Build or load merkle trees for v2/hybrid torrents.

    /// Returns null for v1-only torrents.

    /// </summary>

    internal async Task<Dictionary<SHA256Hash, MerkleTree>?> LoadOrBuildMerkleTreesAsync(

        Torrent torrent, string infoHash, CancellationToken ct)

    {

        if (torrent.Info.Version == TorrentVersion.V1)

            return null;

        // Extract file roots in canonical order from flattened file list

        var files = torrent.Info.Files ?? FileTreeParser.Flatten(torrent.Info.FileTreeV2!);

        var fileRoots = new List<SHA256Hash>();

        foreach (var file in files)

        {

            if (file.PiecesRoot.HasValue)

                fileRoots.Add(file.PiecesRoot.Value);

        }

        if (fileRoots.Count == 0)

            return null;

        // Try loading persisted trees first

        var treeStore = new MerkleTreeStore(_orch.Persistence.ResumeDirectory);

        try

        {

            var loaded = await treeStore.LoadAsync(infoHash, fileRoots, ct).ConfigureAwait(false);

            if (loaded != null)

            {

                var dict = new Dictionary<SHA256Hash, MerkleTree>();

                for (int i = 0; i < fileRoots.Count; i++)

                    dict[fileRoots[i]] = loaded[i];

                _logger.LogDebug("Loaded {Count} persisted merkle trees for {InfoHash}",

                    loaded.Count, infoHash);

                return dict;

            }

        }

        catch (Exception ex)

        {

            _logger.LogWarning(ex, "Failed to load merkle trees for {InfoHash}, rebuilding", infoHash);

        }

        // Build from piece layers in .torrent

        if (torrent.PieceLayers == null || torrent.PieceLayers.Count == 0)

        {

            _logger.LogWarning("V2 torrent {InfoHash} has no piece layers — cannot build trees", infoHash);

            return null;

        }

        var blocksPerPiece = (int)(torrent.Info.PieceLength / MerkleHelpers.BlockSize);

        var trees = new Dictionary<SHA256Hash, MerkleTree>();

        foreach (var root in fileRoots)

        {

            if (!torrent.PieceLayers.TryGetValue(root, out var layerData))

            {

                // Small files (< 1 piece) won't have piece layers — the root IS the piece hash

                trees[root] = MerkleTree.FromPieceLayer(new[] { root }, blocksPerPiece, expectedRoot: root);

                continue;

            }

            // Parse concatenated 32-byte hashes

            var hashCount = layerData.Length / SHA256Hash.Size;

            var pieceHashes = new SHA256Hash[hashCount];

            for (int i = 0; i < hashCount; i++)

                pieceHashes[i] = new SHA256Hash(layerData.AsSpan(i * SHA256Hash.Size, SHA256Hash.Size));

            trees[root] = MerkleTree.FromPieceLayer(pieceHashes, blocksPerPiece, expectedRoot: root);

        }

        _logger.LogDebug("Built {Count} merkle trees from piece layers for {InfoHash}",

            trees.Count, infoHash);

        return trees;

    }

    #endregion

    private static List<(string path, long size)> GetExpectedFilePaths(ManagedTorrent managed)
    {
        var result = new List<(string path, long size)>();
        if (managed.Torrent?.Info == null) return result;

        var info = managed.Torrent.Info;
        var isMultiFile = info.FileMode == TorrentFileMode.Multi;

        foreach (var file in info.Files)
        {
            var filePath = isMultiFile
                ? Path.Combine(managed.SavePath, info.Name, Path.Combine(file.Path.ToArray()))
                : Path.Combine(managed.SavePath, Path.Combine(file.Path.ToArray()));
            result.Add((Path.GetFullPath(filePath), file.Length));
        }

        return result;
    }

    /// <summary>
    /// IProgress&lt;T&gt; that invokes the callback directly on the reporting thread,
    /// without capturing or posting to a SynchronizationContext.
    /// Use instead of <see cref="Progress{T}"/> when callbacks must not marshal to the UI thread.
    /// </summary>
    private sealed class DirectProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public DirectProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

}
