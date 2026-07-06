using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Bencode.Parsers;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Interfaces;
using vTorrent.Core.Session;
using vTorrent.Storage;
using vTorrent.Core.FileAllocator;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Transport;
using vTorrent.Core.PeerCommunication.Transport.Tcp;
using vTorrent.Core.PeerCommunication.Utilities;
using vTorrent.Core.PieceIO;
using vTorrent.Core.PieceIO.Backends;
using vTorrent.Core.Merkle;
using vTorrent.Core.TrackerCommunication;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;
using vTorrent.Core.Download;
using vTorrent.Core.Upload;
using vTorrent.Core.PeerCommunication;
using vTorrent.Core.PeerCommunication.Extensions;

namespace vTorrent.Core.Engine;

/// <summary>
/// Handles the 7-phase initialization of a TorrentEngine.
/// Extracted from TorrentEngine as part of god class decomposition (Phase 5, Task 5.5).
/// </summary>
internal class EnginePhaseInitializer
{
    private readonly TorrentEngine _engine;
    private readonly ILogger _logger;

    internal EnginePhaseInitializer(TorrentEngine engine, ILogger logger)
    {
        _engine = engine;
        _logger = logger;
    }

    internal async Task InitializePhase1_FileAllocationAsync(CancellationToken ct)
    {
        _logger.LogDebug("Phase 1: Preparing file structure (lazy allocation mode)...");

        var fileAllocator = new FileAllocator.FileAllocator();

        var result = await fileAllocator.AllocateFilesAsync(
            _engine.DownloadPathInternal,
            _engine.Torrent.Info,
            AllocationStrategy.None,
            null,
            ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw new InvalidOperationException($"Failed to prepare file structure: {result.ErrorMessage}");

        _engine.SetFileAllocator(fileAllocator);

        _logger.LogDebug("Phase 1 complete: Directory structure prepared for {Count} files",
            _engine.Torrent.Info.Files?.Count ?? 1);
    }

    internal async Task InitializePhase2_CoreComponentsAsync(CancellationToken ct)
    {
        _logger.LogDebug("Phase 2: Creating core components...");

        var loggerFactory = _engine.LoggerFactoryInternal;

        // Statistics - unified tracker + snapshot (no dependencies)
        var torrentStatistics = _engine.TransferAccumulatorInternal != null
            ? new TorrentStatistics(
                loggerFactory.CreateLogger<TorrentStatistics>(),
                _engine.TransferAccumulatorInternal)
            : new TorrentStatistics(
                loggerFactory.CreateLogger<TorrentStatistics>());
        _engine.SetTorrentStatistics(torrentStatistics);

        // Peer registry - centralized peer state tracking
        var peerRegistry = new PeerRegistry();
        _engine.SetPeerRegistry(peerRegistry);

        // Peer cache - persistent storage for fast resume (libtorrent-style)
        if (_engine.DatabaseInternal != null)
        {
            _engine.SetPeerCache(new PeerCache(_engine.DatabaseInternal, loggerFactory.CreateLogger<PeerCache>()));
        }

        var tasks = new List<Task>();

        Task pieceManagerTask = Task.Run(() =>
        {
            var diskSettings = _engine.DiskSettingsInternal;
            var sparseFileManager = new SparseFileManager(_engine.DownloadPathInternal, _engine.Torrent.Info);
            var lockManager = new FileLockManager();
            var backendLogger = loggerFactory.CreateLogger("DiskBackend");

            var diskBackend = DiskBackendFactory.Create(
                diskSettings,
                perTorrentOverride: null,
                perTorrentWriteMode: null,
                sparseFileManager,
                lockManager,
                backendLogger,
                diskMonitor: _engine.DiskMonitorInternal);

            // Wrap backend with partfile-aware decorator for selective download support
            var pieceMapper = new PieceMapper(_engine.DownloadPathInternal, _engine.Torrent.Info);
            var infoHashHex = _engine.Torrent.GetInfoHashHex();
            var initialPriorities = _engine.PendingFilePriorities
                ?? new FilePriority[_engine.Torrent.Info.Files.Count];
            if (_engine.PendingFilePriorities == null)
                Array.Fill(initialPriorities, FilePriority.Normal);

            var partFileBackend = new PartFileAwareDiskBackend(
                diskBackend, pieceMapper, _engine.Torrent.Info,
                _engine.DownloadPathInternal, infoHashHex,
                initialPriorities,
                loggerFactory.CreateLogger("PartFile"));

            _engine.SetDiskBackend(partFileBackend);
            var pieceManager = new PieceManager(
                _engine.DownloadPathInternal,
                _engine.Torrent.Info,
                partFileBackend,
                torrentStatistics,
                skipInitialVerification: true);

            // Create per-torrent DiskWriteThrottler and wire it to PieceManager
            var throttler = new DiskWriteThrottler(diskSettings.MaxQueuedDiskBytes,
                loggerFactory.CreateLogger<DiskWriteThrottler>());
            pieceManager.SetThrottler(throttler);
            _engine.SetDiskWriteThrottler(throttler);

            _engine.SetPieceManager(pieceManager);
        }, ct);
        tasks.Add(pieceManagerTask);

        Task peerManagerTask = Task.Run(() =>
        {
            var transportConnector = _engine.TransportConnectorInternal
                ?? new TcpTransportConnector(_engine.PeerSettingsInternal);
            _engine.SetPeerManager(new PeerManager(
                _engine.Torrent.GetInfoHashBytes(),
                _engine.PeerSettingsInternal,
                loggerFactory,
                peerRegistry,
                transportConnector,
                torrentStatistics,
                bandwidthLimiter: _engine.BandwidthLimiterInternal,
                encryptionMonitor: _engine.EncryptionMonitorInternal,
                connectionMonitor: _engine.ConnectionMonitorInternal,
                externalIpVoter: _engine.ExternalIpVoterInternal,
                privacyMonitor: _engine.PrivacyMonitorInternal,
                peerClassManager: _engine.PeerClassManagerInternal,
                isI2pTorrent: _engine.ManagedTorrentInternal?.IsI2p == true,
                allowMixedMode: _engine.I2pSettingsMonitorInternal?.CurrentValue.AllowMixedMode == true));
        }, ct);
        tasks.Add(peerManagerTask);

        Task trackerManagerTask = Task.Run(() =>
        {
            var bencodeParser = new BencodeParser();
            var trackerFactory = new TrackerClientFactory(
                _engine.TrackerMonitorInternal, loggerFactory, bencodeParser, _engine.PrivacyMonitorInternal,
                _engine.UdpSocketManagerInternal, _engine.TrackerPacketHandlerInternal);
            // Wire I2P for tracker clients — use service for lazy session resolution
            if (_engine.I2pServiceInternal != null)
            {
                trackerFactory.SetI2pService(_engine.I2pServiceInternal);
            }
            var peerKey = RandomNumberGenerator.GetInt32(int.MaxValue);

            _engine.SetTrackerManager(new TrackerManager(
                _engine.Torrent.GetInfoHashBytes(),
                Encoding.ASCII.GetBytes(_engine.PeerSettingsInternal.PeerId),
                _engine.Torrent.GetAllTrackers(),
                trackerFactory,
                _engine.TrackerMonitorInternal,
                loggerFactory.CreateLogger<TrackerManager>(),
                peerKey,
                isPrivateTorrent: _engine.Torrent.Info.IsPrivate,
                externalIpVoter: _engine.ExternalIpVoterInternal,
                isI2pTorrent: _engine.ManagedTorrentInternal?.IsI2p == true,
                allowMixedMode: _engine.I2pSettingsMonitorInternal?.CurrentValue.AllowMixedMode == true));
        }, ct);
        tasks.Add(trackerManagerTask);

        // Web seed manager (if torrent has url-list or httpseeds)
        var torrentForWs = _engine.Torrent;
        if (torrentForWs.UrlList?.Count > 0 || torrentForWs.HttpSeeds?.Count > 0)
        {
            Task webSeedTask = Task.Run(() =>
            {
                var webSeedHandler = new System.Net.Http.SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    MaxConnectionsPerServer = 4,
                    ConnectTimeout = TimeSpan.FromSeconds(_engine.WebSeedSettingsInternal.TimeoutSeconds)
                };
                // Route web-seed HTTP(S) through the configured proxy when peer connections are
                // proxied, so web-seed traffic doesn't leak around the proxy over the real IP.
                // Settings are snapshotted at handler-creation time (matches the tracker factory).
                var wsProxy = _engine.ProxyMonitorInternal?.CurrentValue;
                vTorrent.Core.Network.Proxy.ProxyHttpHandlerConfigurator.Configure(
                    webSeedHandler, wsProxy, wsProxy?.ProxyPeerConnections ?? false);
                var httpClient = new HttpClient(webSeedHandler);
                // AlwaysSendUserAgent: send User-Agent on every request when true (default: false = no default header)
                if (_engine.WebSeedSettingsInternal.AlwaysSendUserAgent)
                    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("vTorrent/1.0");

                _engine.SetWebSeedManager(new WebSeedManager(
                    torrentForWs.UrlList,
                    torrentForWs.HttpSeeds,
                    torrentForWs.Info,
                    torrentForWs.GetInfoHashBytes(),
                    _engine.WebSeedSettingsInternal,
                    httpClient,
                    loggerFactory.CreateLogger<WebSeedManager>(),
                    _engine.TorrentStatisticsInternal));
            }, ct);
            tasks.Add(webSeedTask);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // BEP 52: Create HashPicker and HashExchangeHandler for v2/hybrid torrents
        if (_engine.MerkleTreesInternal != null && _engine.MerkleTreesInternal.Count > 0)
        {
            var torrentInfo = _engine.Torrent.Info;
            var files = torrentInfo.Files ?? FileTreeParser.Flatten(torrentInfo.FileTreeV2!);
            var blocksPerPiece = (int)(torrentInfo.PieceLength / MerkleHelpers.BlockSize);

            var fileRoots = new List<SHA256Hash>();
            var filePieceOffsets = new List<int>();
            int pieceOffset = 0;
            foreach (var file in files)
            {
                if (file.PiecesRoot.HasValue)
                {
                    fileRoots.Add(file.PiecesRoot.Value);
                    filePieceOffsets.Add(pieceOffset);
                }
                pieceOffset += (int)((file.Length + torrentInfo.PieceLength - 1) / torrentInfo.PieceLength);
            }

            var hashPicker = new HashPicker(fileRoots, filePieceOffsets, blocksPerPiece, _engine.Torrent.PieceCount);
            _engine.SetHashPicker(hashPicker);

            var handler = new HashExchangeHandler(_engine.MerkleTreesInternal, hashPicker);
            _engine.SetHashExchangeHandler(handler);

            _engine.PeerManagerInternal.SetHashExchangeHandler(handler);

            _logger.LogDebug("BEP 52: Created HashPicker ({Files} files) and HashExchangeHandler",
                fileRoots.Count);
        }

        // BEP 55: create HolepunchManager if connector is uTP-capable and holepunch is enabled
        if (_engine.TransportConnectorInternal is TransportConnector utpConnector &&
            utpConnector.UtpManager != null &&
            PeerConstants.HolepunchMaxConcurrent > 0)
        {
            var hpLogger = loggerFactory.CreateLogger<HolepunchManager>();
            var holepunchManager = new HolepunchManager(
                hpLogger,
                _engine.PeerManagerInternal,
                utpConnector.UtpManager,
                onHolepunchConnected: (stream, endpoint) =>
                {
                    // Target role: a peer connected to us via holepunch — add as peer
                    _logger.LogDebug("Holepunch inbound connection from {Endpoint}", endpoint);
                },
                maxConcurrentAttempts: PeerConstants.HolepunchMaxConcurrent,
                cooldownSeconds: PeerConstants.HolepunchCooldownSeconds);
            _engine.SetHolepunchManager(holepunchManager);
            utpConnector.SetHolepunchManager(holepunchManager);
            _logger.LogDebug("BEP 55: HolepunchManager created");
        }

        _logger.LogDebug("Phase 2 complete");
    }

    internal async Task<bool> TryFastResumePhase3Async(CancellationToken ct)
    {
        var pieceCount = _engine.Torrent.PieceCount;
        _logger.LogWarning("[DIAG] TryFastResumePhase3: creating empty bitfield with pieceCount={PieceCount}, hasResumeProvider={HasProvider}",
            pieceCount, _engine.ResumeDataProviderInternal != null);
        _engine.SetLocalBitfield(new Bitfield(pieceCount));

        // libtorrent parity: verify_resume_data() ALWAYS validates file existence
        // before any resume path. This catches missing files regardless of which
        // fast-resume path would run (SeedMode, NoVerifyFiles, crash recovery, etc.)
        var missingFiles = ValidateFilesExist(
            _engine.DownloadPathInternal, _engine.Torrent.Info, bitfield: null);

        if (missingFiles.Count > 0)
        {
            var firstName = Path.GetFileName(missingFiles[0].path);
            var message = missingFiles.Count == 1
                ? $"File not found: {firstName}"
                : $"{missingFiles.Count} files not found (first: {firstName})";

            _logger.LogWarning("Files missing at save path '{SavePath}': {Message}",
                _engine.DownloadPathInternal, message);
            foreach (var (path, expected, actual) in missingFiles.Take(5))
            {
                if (actual < 0)
                    _logger.LogWarning("  Missing: {Path}", path);
                else
                    _logger.LogWarning("  Too small: {Path} ({Actual} < {Expected} bytes)", path, actual, expected);
            }

            _engine.RaiseMissingFilesDetected(message, missingFiles);
            // Don't return false here — let the normal flow continue.
            // The health status is set, and verification will confirm 0 pieces.
            // This way the torrent shows "Missing Files" in the UI.
        }

        if (_engine.ResumeDataProviderInternal != null)
        {
            return await TryFastResumeAsync(ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Verifies all pieces using batched parallel hashing.
    /// Each worker thread processes a contiguous range sequentially.
    /// Blocks until all batches complete — download does not start until done.
    /// </summary>
    internal async Task VerifyPiecesAsync(CancellationToken ct)
    {
        var pieceCount = _engine.Torrent.PieceCount;
        if (pieceCount == 0) return;

        // Pre-check: log if any torrent files are missing before verification
        if (_engine.PieceManagerInternal != null)
        {
            var basePath = _engine.DownloadPathInternal;
            var torrentInfo = _engine.Torrent.Info;
            var isMultiFile = torrentInfo.FileMode == TorrentFileMode.Multi;

            int missingCount = 0;
            foreach (var file in torrentInfo.Files)
            {
                var filePath = isMultiFile
                    ? Path.GetFullPath(Path.Combine(basePath, torrentInfo.Name, Path.Combine(file.Path.ToArray())))
                    : Path.GetFullPath(Path.Combine(basePath, Path.Combine(file.Path.ToArray())));
                if (!File.Exists(filePath))
                {
                    if (missingCount < 5)
                        _logger.LogWarning("Verification: expected file not found: {Path}", filePath);
                    missingCount++;
                }
            }
            if (missingCount > 5)
                _logger.LogWarning("Verification: {Count} more files not found (showing first 5)", missingCount - 5);
            if (missingCount > 0)
                _logger.LogWarning("Verification will report 0 verified pieces — files not at save path '{Path}'", basePath);
        }

        var workerCount = Math.Max(1, Environment.ProcessorCount / 2);
        var batchSize = (int)Math.Ceiling((double)pieceCount / workerCount);
        int verifiedCount = 0;
        int checkedCount = 0;

        var tasks = new Task[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            int start = w * batchSize;
            int end = Math.Min(start + batchSize, pieceCount);
            if (start >= pieceCount)
            {
                tasks[w] = Task.CompletedTask;
                continue;
            }

            tasks[w] = Task.Run(async () =>
            {
                for (int i = start; i < end; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var readResult = await _engine.PieceManagerInternal
                        .ReadPieceAsync(i).ConfigureAwait(false);

                    // Trust the hash, not the byte-content. A piece whose data is
                    // legitimately all zeros (common in ISO images and other files with
                    // zero-filled regions) still hashes correctly and must be marked present.
                    // libtorrent parity: torrent::on_piece_hashed accepts a piece purely on
                    // `piece_hash == hash_for_piece(piece)` and never inspects the data.
                    if (readResult.IsSuccess &&
                        _engine.PieceManagerInternal.VerifyPiece(i, readResult.Data))
                    {
                        _engine.LocalBitfieldInternal.SetPiece(i);
                        _engine.OnPieceVerified(i);
                        Interlocked.Increment(ref verifiedCount);
                    }

                    Interlocked.Increment(ref checkedCount);
                }
            }, ct);
        }

        // Report progress periodically while waiting
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = Task.Run(async () =>
        {
            while (!progressCts.Token.IsCancellationRequested)
            {
                _engine.TorrentStatisticsInternal.VerificationProgress =
                    (double)Volatile.Read(ref checkedCount) / pieceCount;
                try { await Task.Delay(250, progressCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, progressCts.Token);

        await Task.WhenAll(tasks).ConfigureAwait(false);

        progressCts.Cancel();
        try { await progressTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        _engine.TorrentStatisticsInternal.VerificationProgress = 1.0;
        SyncPieceManagerBitfield();

        _logger.LogDebug("Verification complete: {Verified}/{Total} pieces",
            _engine.LocalBitfieldInternal.CompletePieces, pieceCount);
    }

    /// <summary>
    /// Starts piece verification as a background task. Returns immediately.
    /// Verified pieces become available progressively via Bitfield.SetPiece().
    /// The engine's VerificationDone TCS is set when all pieces are checked.
    /// After verification, evaluates completion to transition to seeding if appropriate
    /// (libtorrent parity: files_checked → is_seed → completed).
    /// </summary>
    internal void StartBackgroundVerification(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await VerifyPiecesAsync(ct).ConfigureAwait(false);
                _engine.VerificationDone.TrySetResult();

                // libtorrent parity: after verification, evaluate if all wanted pieces
                // are present and transition to seeding if so.
                // Equivalent to libtorrent's files_checked() → is_seed() → completed().
                await _engine.EvaluateCompletionAfterVerification().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _engine.VerificationDone.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background verification failed");
                _engine.VerificationDone.TrySetException(ex);
            }
        }, ct);
    }

    /// <summary>
    /// Validate that torrent files exist at the save path with correct sizes.
    /// libtorrent parity: verify_resume_data() in storage_utils.cpp.
    ///
    /// If bitfield is null, checks ALL files (seed mode path).
    /// If bitfield is provided, checks only files with at least one "have" piece.
    /// </summary>
    private List<(string path, long expectedSize, long actualSize)> ValidateFilesExist(
        string savePath, TorrentInfo torrentInfo, Bitfield? bitfield)
    {
        var missing = new List<(string path, long expectedSize, long actualSize)>();
        var isSingleFile = torrentInfo.FileMode == Bencode.Torrents.TorrentFileMode.Single;

        if (isSingleFile)
        {
            var filePath = Path.GetFullPath(Path.Combine(savePath, torrentInfo.Name));
            if (bitfield == null || bitfield.CompletePieces > 0)
            {
                if (!File.Exists(filePath))
                {
                    missing.Add((filePath, torrentInfo.TotalSize, -1));
                }
                else
                {
                    var actualSize = new FileInfo(filePath).Length;
                    if (actualSize < torrentInfo.TotalSize)
                        missing.Add((filePath, torrentInfo.TotalSize, actualSize));
                }
            }
        }
        else if (torrentInfo.Files != null)
        {
            var torrentDir = Path.Combine(savePath, torrentInfo.Name);

            HashSet<int>? relevantFileIndices = null;
            if (bitfield != null)
            {
                relevantFileIndices = new HashSet<int>();
                long offset = 0;
                for (int fi = 0; fi < torrentInfo.Files.Count; fi++)
                {
                    var file = torrentInfo.Files[fi];
                    long fileEnd = offset + file.Length;

                    int firstPiece = (int)(offset / torrentInfo.PieceLength);
                    int lastPiece = Math.Min((int)((fileEnd - 1) / torrentInfo.PieceLength), bitfield.PieceCount - 1);

                    for (int p = firstPiece; p <= lastPiece; p++)
                    {
                        if (bitfield.HasPiece(p))
                        {
                            relevantFileIndices.Add(fi);
                            break;
                        }
                    }
                    offset = fileEnd;
                }
            }

            for (int fi = 0; fi < torrentInfo.Files.Count; fi++)
            {
                if (relevantFileIndices != null && !relevantFileIndices.Contains(fi))
                    continue;

                var file = torrentInfo.Files[fi];
                var filePath = Path.GetFullPath(Path.Combine(torrentDir, Path.Combine(file.Path.ToArray())));

                if (!File.Exists(filePath))
                {
                    missing.Add((filePath, file.Length, -1));
                }
                else
                {
                    var actualSize = new FileInfo(filePath).Length;
                    if (actualSize < file.Length)
                        missing.Add((filePath, file.Length, actualSize));
                }
            }
        }
        else
        {
            // v2/hybrid torrents may use FileTree instead of Files list.
            // Cannot validate without file list — log and allow through.
            _logger.LogDebug("ValidateFilesExist: no file list available for {Mode} torrent, skipping validation",
                torrentInfo.FileMode);
        }

        return missing;
    }

    internal void SyncPieceManagerBitfield()
    {
        if (_engine.PieceManagerInternal == null || _engine.LocalBitfieldInternal == null)
            return;

        var bitfield = _engine.LocalBitfieldInternal;
        var bitArray = new BitArray(bitfield.PieceCount);
        for (int i = 0; i < bitfield.PieceCount; i++)
        {
            bitArray[i] = bitfield.HasPiece(i);
        }

        _engine.PieceManagerInternal.InitializeFromResumeBitfield(bitArray);
        _logger.LogDebug("Synced {Count} completed pieces to PieceManager", bitfield.CompletePieces);
    }

    private async Task<bool> TryFastResumeAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resumeDataProvider = _engine.ResumeDataProviderInternal;
        if (resumeDataProvider == null)
            return false;

        try
        {
            var flags = await resumeDataProvider.GetFlagsAsync().ConfigureAwait(false);

            // 1. Seed mode: all pieces assumed present, verify lazily on upload (libtorrent parity)
            // Checked FIRST to match libtorrent's on_resume_data_checked() priority.
            // SeedMode torrents must always take this path to set IsSeedMode and the lazy tracker,
            // even when NoVerifyFiles is also set (graceful shutdown sets both).
            if (flags.HasFlag(ResumeData.TorrentFlags.SeedMode))
            {
                // libtorrent parity: verify_resume_data() validates ALL files exist
                // before accepting seed mode (storage_utils.cpp:502-533).
                var missingFiles = ValidateFilesExist(
                    _engine.DownloadPathInternal, _engine.Torrent.Info, bitfield: null);

                if (missingFiles.Count > 0)
                {
                    var firstName = System.IO.Path.GetFileName(missingFiles[0].path);
                    var message = missingFiles.Count == 1
                        ? $"File not found: {firstName}"
                        : $"{missingFiles.Count} files not found (first: {firstName})";

                    _logger.LogWarning("Seed mode rejected for {Name}: {Message}",
                        _engine.Torrent.DisplayName, message);
                    foreach (var (path, expected, actual) in missingFiles.Take(5))
                    {
                        if (actual < 0)
                            _logger.LogWarning("  Missing: {Path}", path);
                        else
                            _logger.LogWarning("  Too small: {Path} ({Actual} < {Expected} bytes)", path, actual, expected);
                    }

                    _engine.RaiseMissingFilesDetected(message, missingFiles);
                    _logger.LogDebug("Fast resume rejected in {ElapsedMs}ms (path: SeedMode files missing)",
                        sw.ElapsedMilliseconds);
                    return false;
                }

                _engine.IsSeedMode = true;

                // Set bitfield to all-ones — all pieces assumed present
                _engine.LocalBitfieldInternal.SetAll();

                // Load or create verified-pieces tracker (reads VerifiedPieces only)
                var verifiedBitfield = await resumeDataProvider.LoadVerifiedPiecesAsync().ConfigureAwait(false);
                _engine.SeedModeVerifiedPieces = verifiedBitfield ?? new Bitfield(_engine.Torrent.PieceCount);

                _logger.LogInformation("Seed mode: all {Count} pieces assumed present, {Verified} previously verified",
                    _engine.Torrent.PieceCount, _engine.SeedModeVerifiedPieces.CompletePieces);
                _logger.LogDebug("Fast resume completed in {ElapsedMs}ms (path: SeedMode, {Count} pieces)",
                    sw.ElapsedMilliseconds, _engine.Torrent.PieceCount);

                return true;
            }

            // 2. NoVerifyFiles: clean shutdown — trust HavePieces bitfield
            _logger.LogWarning("[DIAG] TryFastResume: flags={Flags}, hasSeedMode={SeedMode}, hasNoVerify={NoVerify}",
                flags, flags.HasFlag(ResumeData.TorrentFlags.SeedMode), flags.HasFlag(ResumeData.TorrentFlags.NoVerifyFiles));
            if (flags.HasFlag(ResumeData.TorrentFlags.NoVerifyFiles))
            {
                var savedBitfield = await resumeDataProvider.LoadHavePiecesAsync().ConfigureAwait(false);
                _logger.LogWarning("[DIAG] TryFastResume NoVerifyFiles: savedBitfield null={IsNull}, completePieces={Complete}, pieceCount={PieceCount}, isComplete={IsComplete}",
                    savedBitfield == null,
                    savedBitfield?.CompletePieces ?? -1,
                    savedBitfield?.PieceCount ?? -1,
                    savedBitfield?.IsComplete ?? false);
                if (savedBitfield != null)
                {
                    // libtorrent parity: verify_resume_data() validates files with "have"
                    // pieces exist (storage_utils.cpp:544-575)
                    var missingFiles = ValidateFilesExist(
                        _engine.DownloadPathInternal, _engine.Torrent.Info, savedBitfield);

                    if (missingFiles.Count > 0)
                    {
                        var firstName = System.IO.Path.GetFileName(missingFiles[0].path);
                        var message = missingFiles.Count == 1
                            ? $"File not found: {firstName}"
                            : $"{missingFiles.Count} files not found (first: {firstName})";

                        _logger.LogWarning("Fast resume rejected (NoVerifyFiles): {Message}", message);
                        _engine.RaiseMissingFilesDetected(message, missingFiles);
                        _logger.LogDebug("Fast resume rejected in {ElapsedMs}ms (path: NoVerifyFiles files missing)",
                            sw.ElapsedMilliseconds);
                        return false;
                    }

                    _engine.SetLocalBitfield(savedBitfield);
                    _logger.LogWarning("[DIAG] TryFastResume: SUCCESS via NoVerifyFiles. bitfield ref={Ref}, complete={Complete}/{Total}, IsComplete={IsComplete}",
                        _engine.LocalBitfieldInternal.GetHashCode(),
                        _engine.LocalBitfieldInternal.CompletePieces,
                        _engine.Torrent.PieceCount,
                        _engine.LocalBitfieldInternal.IsComplete);
                    _logger.LogDebug("Fast resume completed in {ElapsedMs}ms (path: NoVerifyFiles, {Count} pieces)",
                        sw.ElapsedMilliseconds, _engine.LocalBitfieldInternal.CompletePieces);
                    return true;
                }
            }

            // 3. Crash recovery needed
            var needsCrashRecovery = await resumeDataProvider.NeedsCrashRecoveryAsync().ConfigureAwait(false);
            if (needsCrashRecovery)
            {
                if (_engine.DiskSettingsInternal.NoRecheckIncompleteResume)
                {
                    var crashBitfield = await resumeDataProvider.LoadHavePiecesAsync().ConfigureAwait(false);
                    if (crashBitfield != null && crashBitfield.CompletePieces > 0)
                    {
                        _engine.SetLocalBitfield(crashBitfield);
                        _logger.LogWarning("Fast resume: NoRecheckIncompleteResume set, trusting {Count} pieces despite crash recovery flag",
                            _engine.LocalBitfieldInternal.CompletePieces);
                        _logger.LogDebug("Fast resume completed in {ElapsedMs}ms (path: NoRecheckIncompleteResume crash, {Count} pieces)",
                            sw.ElapsedMilliseconds, _engine.LocalBitfieldInternal.CompletePieces);
                        return true;
                    }
                }
                _logger.LogWarning("Fast resume skipped: crash recovery needed");
                _logger.LogDebug("Fast resume skipped in {ElapsedMs}ms (path: crash recovery)", sw.ElapsedMilliseconds);
                return false;
            }

            // 4. Files modified since last save
            var filesModified = await _engine.CheckFilesModifiedInternalAsync(ct).ConfigureAwait(false);
            if (filesModified)
            {
                if (_engine.DiskSettingsInternal.NoRecheckIncompleteResume)
                {
                    var modifiedBitfield = await resumeDataProvider.LoadHavePiecesAsync().ConfigureAwait(false);
                    if (modifiedBitfield != null && modifiedBitfield.CompletePieces > 0)
                    {
                        _engine.SetLocalBitfield(modifiedBitfield);
                        _logger.LogWarning("Fast resume: NoRecheckIncompleteResume set, trusting {Count} pieces despite file modification",
                            _engine.LocalBitfieldInternal.CompletePieces);
                        _logger.LogDebug("Fast resume completed in {ElapsedMs}ms (path: NoRecheckIncompleteResume files modified, {Count} pieces)",
                            sw.ElapsedMilliseconds, _engine.LocalBitfieldInternal.CompletePieces);
                        return true;
                    }
                }
                _logger.LogWarning("Fast resume skipped: files modified since last save");
                _logger.LogDebug("Fast resume skipped in {ElapsedMs}ms (path: files modified)", sw.ElapsedMilliseconds);
                return false;
            }

            // 5. Load saved bitfield and trust it
            var resumeBitfield = await resumeDataProvider.LoadHavePiecesAsync().ConfigureAwait(false);
            if (resumeBitfield == null || resumeBitfield.CompletePieces == 0)
            {
                _logger.LogDebug("Fast resume skipped: no saved bitfield available");
                _logger.LogDebug("Fast resume skipped in {ElapsedMs}ms (path: no saved bitfield)", sw.ElapsedMilliseconds);
                return false;
            }

            // libtorrent parity: verify_resume_data() validates files with "have"
            // pieces exist (storage_utils.cpp:544-575)
            var trustedMissing = ValidateFilesExist(
                _engine.DownloadPathInternal, _engine.Torrent.Info, resumeBitfield);

            if (trustedMissing.Count > 0)
            {
                var firstName = System.IO.Path.GetFileName(trustedMissing[0].path);
                var message = trustedMissing.Count == 1
                    ? $"File not found: {firstName}"
                    : $"{trustedMissing.Count} files not found (first: {firstName})";

                _logger.LogWarning("Fast resume rejected (trusted bitfield): {Message}", message);
                _engine.RaiseMissingFilesDetected(message, trustedMissing);
                _logger.LogDebug("Fast resume rejected in {ElapsedMs}ms (path: trusted bitfield files missing)",
                    sw.ElapsedMilliseconds);
                return false;
            }

            _engine.SetLocalBitfield(resumeBitfield);
            _logger.LogDebug("Fast resume successful: {Count}/{Total} pieces restored from resume data",
                _engine.LocalBitfieldInternal.CompletePieces, _engine.Torrent.PieceCount);
            _logger.LogDebug("Fast resume completed in {ElapsedMs}ms (path: trusted bitfield, {Count}/{Total} pieces)",
                sw.ElapsedMilliseconds, _engine.LocalBitfieldInternal.CompletePieces, _engine.Torrent.PieceCount);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fast resume failed, falling back to full verification");
            return false;
        }
    }

    internal async Task InitializePhase4_NetworkAsync(CancellationToken ct)
    {
        _logger.LogDebug("Phase 4: Starting network components...");

        var loggerFactory = _engine.LoggerFactoryInternal;

        _engine.SetMessageRouter(new PeerMessageRouter(
            _engine.PeerManagerInternal,
            loggerFactory.CreateLogger<PeerMessageRouter>()));

        // Restore cached peers BEFORE starting network (instant peer availability)
        if (_engine.PeerCacheInternal != null)
        {
            var restoredCount = await _engine.PeerCacheInternal.RestorePeersToRegistryAsync(
                _engine.InfoHashHex, _engine.PeerRegistryInternal, ct).ConfigureAwait(false);
            if (restoredCount > 0)
            {
                _logger.LogDebug("Restored {Count} peers from cache for instant connectivity", restoredCount);
            }
        }

        await _engine.PeerManagerInternal.StartAsync(ct).ConfigureAwait(false);
        // TrackerManager already started during Phase 3 (verification) for warm peer lists

        _logger.LogDebug("Phase 4 complete");
    }

    internal void InitializePhase5_Coordinators()
    {
        _logger.LogDebug("Phase 5: Creating coordinators...");

        var loggerFactory = _engine.LoggerFactoryInternal;
        var torrent = _engine.Torrent;
        var peerSettings = _engine.PeerSettingsInternal;

        // Set torrent metadata on the statistics instance (created in Phase 2)
        _engine.TorrentStatisticsInternal.TotalSize = torrent.TotalSize;
        _engine.TorrentStatisticsInternal.TotalPieces = torrent.PieceCount;
        _engine.TorrentStatisticsInternal.AddedTime = DateTime.UtcNow;

        // Initialize file progress tracker for per-file progress
        var fileProgressTracker = new FileProgressTracker(torrent.Info);
        _engine.SetFileProgressTracker(fileProgressTracker);

        // Apply pending file priorities BEFORE the download loop starts.
        // This follows libtorrent's model: file_priority(0) = don't download.
        // Priorities must be set here so the piece picker never considers
        // pieces belonging exclusively to skipped files.
        if (_engine.PendingFilePriorities != null)
        {
            var pending = _engine.PendingFilePriorities;
            fileProgressTracker.SetFilePriorities(pending);
            // Update TotalWanted to reflect only wanted files
            _engine.TorrentStatisticsInternal.TotalWanted = fileProgressTracker.GetTotalWantedBytes();

            _engine.SetPendingFilePriorities(null); // consumed
            _logger.LogDebug("Applied pending file priorities ({SkipCount} files skipped)",
                pending.Count(p => p == FilePriority.Skip));
        }

        // If we have resume data, initialize file progress from bitfield
        var localBitfield = _engine.LocalBitfieldInternal;
        _logger.LogWarning("[DIAG] Phase 5: localBitfield ref={Ref}, complete={Complete}/{Total}, IsComplete={IsComplete}",
            localBitfield?.GetHashCode() ?? 0,
            localBitfield?.CompletePieces ?? -1,
            localBitfield?.PieceCount ?? -1,
            localBitfield?.IsComplete ?? false);
        if (localBitfield != null)
        {
            var havePieces = new bool[torrent.PieceCount];
            for (int i = 0; i < torrent.PieceCount; i++)
            {
                havePieces[i] = localBitfield.HasPiece(i);
            }
            fileProgressTracker.InitializeFromBitfield(havePieces);
        }

        var behaviorMonitor = _engine.BehaviorMonitorInternal;
        var peerSettingsMonitor = _engine.PeerSettingsMonitorInternal;

        var chokingManager = new ChokingManager(
            _engine.PeerManagerInternal,
            _engine.TorrentStatisticsInternal,
            () => _engine.Phase == TransferPhase.Seeding,
            loggerFactory.CreateLogger<ChokingManager>(),
            behaviorMonitor,
            peerSettingsMonitor,
            unchokeAllocator: _engine.UnchokeAllocatorInternal);
        _engine.SetChokingManager(chokingManager);

        // Configure choking manager with upload slots and algorithm settings
        var behaviorSettings = behaviorMonitor?.CurrentValue ?? new BehaviorSettings();
        chokingManager.Configure(
            algorithm: behaviorSettings.ChokingAlgorithm,
            seedAlgorithm: behaviorSettings.SeedChokingAlgorithm,
            maxSlots: peerSettings.MaxUploadsPerTorrent,
            minSlots: Math.Min(2, peerSettings.MaxUploadsPerTorrent),
            rechokingInterval: TimeSpan.FromSeconds(peerSettings.UnchokeInterval),
            optimisticRotationInterval: TimeSpan.FromSeconds(peerSettings.OptimisticUnchokeInterval));

        // Initialize seeder swarm detector
        _engine.SetSeederSwarmDetector(new SeederSwarmDetector(
            _engine.TorrentStatisticsInternal,
            loggerFactory.CreateLogger<SeederSwarmDetector>(),
            _engine.OnSeederSwarmStateChangedInternal));

        // Endgame strategy
        IEndgameStrategy endgameStrategy = new EndgameManager(
            loggerFactory.CreateLogger<EndgameManager>());

        // Explicit DiskWriteCache creation for lifecycle management (owned here, not inside coordinator)
        var diskWriteCache = new DiskWriteCache(peerSettings.DiskCacheSize);

        var downloadCoordinator = new DownloadCoordinator(
            _engine.PeerManagerInternal,
            _engine.PieceManagerInternal,
            _engine.TorrentStatisticsInternal,
            endgameStrategy,
            localBitfield,
            torrent.Info,
            peerSettings,
            _engine.PeerRegistryInternal,
            loggerFactory.CreateLogger<DownloadCoordinator>(),
            diskWriteCache,
            behaviorMonitor);
        _engine.SetDownloadCoordinator(downloadCoordinator);

        if (_engine.HashPickerInternal != null)
            _engine.DownloadCoordinatorInternal.HashPickerInstance = _engine.HashPickerInternal;

        // Wire disk write throttler to PieceCompletionManager for backpressure
        if (_engine.DiskWriteThrottlerInternal != null)
            downloadCoordinator.PieceCompletionManager.SetThrottler(_engine.DiskWriteThrottlerInternal);

        // Wire hash verification pipeline to PieceCompletionManager for offloaded hashing
        if (_engine.PieceManagerInternal is PieceManager concreteManager)
        {
            var diskSettings = _engine.DiskSettingsInternal;
            var hashThreads = diskSettings.HashThreads > 0 ? diskSettings.HashThreads : 2;
            var verificationPipeline = concreteManager.GetOrCreateDownloadVerificationPipeline(hashThreads);
            verificationPipeline.StartDownloadVerification();
            downloadCoordinator.PieceCompletionManager.SetVerificationPipeline(verificationPipeline);
        }

        // Wire message router so web seed events route through PeerMessageRouter
        downloadCoordinator.SetMessageRouter(_engine.MessageRouterInternal);

        // Wire file progress tracker for file-aware piece selection
        downloadCoordinator.SetFileProgressTracker(fileProgressTracker);

        // Wire bitfield provider so PeerManager sends our bitfield after handshake
        _engine.PeerManagerInternal.SetLocalBitfieldProvider(
            () => downloadCoordinator.GetBitfieldBytes());

        // Apply sequential download setting
        if (_engine.SequentialDownloadSettingInternal)
        {
            downloadCoordinator.SetSequentialMode(true);
            _logger.LogDebug("Sequential download mode enabled for {Name}", torrent.DisplayName);
        }

        // Wire streaming manager for piece-deadline API
        var streamingManager = new Streaming.StreamingManager(torrent.Info.Pieces.Count);
        downloadCoordinator.SetStreamingManager(streamingManager);

        // Send buffer flow control — guided read-ahead for uploads
        PeerSendBufferManager? sendBufferManager = null;
        var pieceManager = _engine.PieceManagerInternal as PieceManager;
        var diskBackend = _engine.DiskBackendInternal;
        if (pieceManager != null && diskBackend != null)
        {
            sendBufferManager = new PeerSendBufferManager(
                diskBackend,
                pieceManager.PieceMapperInternal,
                peerSettingsMonitor ?? new OptionsMonitorShim<PeerSettings>(peerSettings),
                torrent.Info,
                loggerFactory.CreateLogger<PeerSendBufferManager>(),
                _engine.StopToken);
            _engine.SetSendBufferManager(sendBufferManager);

            // Wire unchoke/choke/disconnect/state events to send buffer manager
            chokingManager.PeerUnchoked += sendBufferManager.OnPeerUnchoked;
            chokingManager.PeerChoked += sendBufferManager.OnPeerChoked;
            _engine.PeerManagerInternal.PeerDisconnected += sendBufferManager.OnPeerDisconnected;
            // StatusUpdated removed (Task 7); CancelAll is called explicitly in StopAsync.
        }

        var uploadCoordinator = new UploadCoordinator(
            _engine.PeerManagerInternal,
            _engine.PieceManagerInternal,
            chokingManager,
            _engine.TorrentStatisticsInternal,
            torrent.Info,
            pieceIndex => localBitfield.HasPiece(pieceIndex),
            loggerFactory.CreateLogger<UploadCoordinator>());
        if (sendBufferManager != null)
            uploadCoordinator.SetSendBufferManager(sendBufferManager);

        // Seed mode: create and wire lazy verifier if engine is in seed mode
        if (_engine.IsSeedMode && _engine.SeedModeVerifiedPieces != null && torrent.Info.Pieces != null)
        {
            var pieceHashes = new byte[torrent.PieceCount][];
            for (int i = 0; i < torrent.PieceCount; i++)
            {
                pieceHashes[i] = torrent.Info.Pieces.GetPieceHash(i).ToArray();
            }

            var seedVerifier = new SeedModeVerifier(
                _engine.SeedModeVerifiedPieces,
                _engine.PieceManagerInternal,
                pieceHashes,
                torrent.PieceCount,
                loggerFactory.CreateLogger<SeedModeVerifier>());
            uploadCoordinator.SetSeedModeVerifier(seedVerifier);
            _engine.SetSeedModeVerifier(seedVerifier);

            seedVerifier.SeedModeAborted += (_, _) =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _engine.ExitSeedModeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to exit seed mode cleanly");
                    }
                });
            };
        }

        _engine.SetUploadCoordinator(uploadCoordinator);

        // Wire BEP 54 DONTHAVE callback for upload read failures
        uploadCoordinator.SetDontHaveCallback(async (peer, pieceIndex) =>
        {
            if (_engine.DontHaveExtensions.TryGetValue(peer, out var ext))
            {
                await ext.SendDontHaveAsync(pieceIndex).ConfigureAwait(false);
            }
        });

        // Wire FileReadFailed to surface upload read errors to the orchestrator
        uploadCoordinator.FileReadFailed += (sender, e) =>
        {
            _logger.LogWarning("File read failed during upload: piece {Piece} — {Error}",
                e.PieceIndex, e.ErrorMessage);
        };

        // BEP 16: Super-seeding manager
        var superSeedLogger = loggerFactory.CreateLogger<SuperSeedManager>();
        var superSeedManager = new SuperSeedManager(
            torrent.PieceCount,
            _engine.PeerManagerInternal,
            superSeedLogger);
        _engine.SetSuperSeedManager(superSeedManager);

        _engine.SetPeerProber(new PeerReplacer(
            _engine.PeerManagerInternal,
            _engine.TorrentStatisticsInternal,
            () => _engine.Phase == TransferPhase.Seeding,
            loggerFactory.CreateLogger<PeerReplacer>(),
            _engine.BehaviorMonitorInternal));

        // BEP 52: wire peer prober to download coordinator for hash exchange
        _engine.DownloadCoordinatorInternal.PeerProber = _engine.PeerProberInternal;

        // Wire web seed manager to download coordinator
        if (_engine.WebSeedManagerInternal != null)
        {
            downloadCoordinator.SetWebSeedManager(_engine.WebSeedManagerInternal);
        }

        _logger.LogDebug("Phase 5 complete");
    }

    internal void InitializePhase6_WireMessageHandlers()
    {
        _logger.LogDebug("Phase 6: Wiring message handlers...");

        // Self-registration pattern: coordinators register their own handlers
        _engine.DownloadCoordinatorInternal.RegisterHandlers(_engine.MessageRouterInternal);
        _engine.UploadCoordinatorInternal.RegisterHandlers(_engine.MessageRouterInternal);
        _engine.ChokingManagerInternal.RegisterHandlers(_engine.MessageRouterInternal);

        // BEP 16: Route incoming HAVE to SuperSeedManager for propagation tracking
        _engine.MessageRouterInternal.RegisterHandler(MessageType.Have, async (peer, msg) =>
        {
            var superSeedManager = _engine.SuperSeedManagerInternal;
            if (superSeedManager == null || !superSeedManager.IsEnabled)
                return;

            var pieceIndex = msg.ParseHave();
            var reveals = superSeedManager.OnHaveReceived(peer, pieceIndex);
            if (reveals != null)
            {
                foreach (var (targetPeer, revealPiece) in reveals)
                {
                    try
                    {
                        await targetPeer.AnnounceHaveAsync(revealPiece).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Peer may have disconnected
                    }
                }
            }
        });

        // Tracker peer discovery
        _engine.TrackerManagerInternal.PeersDiscovered += _engine.OnPeersDiscoveredInternal;

        // Download completion
        _engine.DownloadCoordinatorInternal.PieceCompleted += _engine.OnPieceCompletedInternal;
        _engine.DownloadCoordinatorInternal.DownloadCompleted += _engine.OnDownloadCompletedInternal;

        // Disk write error → error state (libtorrent file_error_alert pattern)
        _engine.DownloadCoordinatorInternal.DiskWriteError += _engine.OnDiskWriteErrorInternal;

        // BEP 54: register DontHaveExtension on each new peer (before handshake)
        var loggerFactory = _engine.LoggerFactoryInternal;
        var downloadCoordinator = _engine.DownloadCoordinatorInternal;
        var torrent = _engine.Torrent;

        _engine.PeerManagerInternal.SetPeerExtensionSetup(peerConn =>
        {
            if (peerConn is not PeerConnection peerConnection)
                return;

            var dontHaveLogger = loggerFactory.CreateLogger<DontHaveExtension>();
            var dontHaveExt = new DontHaveExtension(
                dontHaveLogger,
                pieceIndex => downloadCoordinator.OnPeerLostPiece(peerConn, pieceIndex),
                async msg => await peerConn.SendMessageAsync(msg),
                torrent.Info.PieceCount);
            peerConnection.RegisterExtension(dontHaveExt);
            _engine.DontHaveExtensions[peerConn] = dontHaveExt;

            // Clean up when this peer disconnects
            peerConn.ConnectionLost += (_, _) => _engine.DontHaveExtensions.TryRemove(peerConn, out _);

            // BEP 55: Register HolepunchExtension per peer
            if (_engine.HolepunchManagerInternal != null)
            {
                var hpLogger = loggerFactory.CreateLogger<HolepunchExtension>();
                var holepunchExt = new HolepunchExtension(
                    hpLogger,
                    (sender, msg) => { _ = _engine.HolepunchManagerInternal.HandleMessageAsync(sender, msg); },
                    async msg => await peerConn.SendMessageAsync(msg),
                    isEnabled: true);
                peerConnection.RegisterExtension(holepunchExt);
            }

            // BEP 10: Register PEX extension — I2P-aware branching
            if (!torrent.Info.IsPrivate)
            {
                var isI2pTorrent = _engine.ManagedTorrentInternal?.IsI2p == true;
                var isI2pPeer = peerConn.PeerInfo.IsI2p;
                var allowMixedMode = _engine.I2pSettingsMonitorInternal?.CurrentValue.AllowMixedMode == true;

                var pexName = PexRegistrationHelper.GetPexExtensionName(isI2pTorrent, isI2pPeer, allowMixedMode);

                if (pexName == "ut_pex")
                {
                    var pexLogger = loggerFactory.CreateLogger<PexExtension>();
                    var pexExt = new PexExtension(
                        pexLogger,
                        () => _engine.PeerManagerInternal.ConnectedPeers
                            .Where(p => !p.PeerInfo.IsI2p)
                            .Select(p => new PexPeerInfo(p.PeerInfo.EndPoint))
                            .ToList(),
                        entries => { },
                        isPrivateTorrent: torrent.Info.IsPrivate);
                    peerConnection.RegisterExtension(pexExt);
                }
                // I2P PEX (i2p_pex) requires I2pPexExtension to implement IExtension — deferred
                // null → rejected peer, no PEX
            }
        });

        // BEP 54: subscribe to piece hash failures for DONTHAVE broadcasting
        downloadCoordinator.PieceCompletionManager.PieceLost += pieceIndex => _engine.BroadcastDontHave(pieceIndex);

        // BEP 16: Send HAVE_NONE + initial piece to new peers during super-seeding
        _engine.PeerManagerInternal.PeerConnected += async (sender, e) =>
        {
            var ssm = _engine.SuperSeedManagerInternal;
            if (ssm == null || !ssm.IsEnabled) return;

            var peer = e.Peer;
            if (peer == null || !peer.IsConnected) return;

            try
            {
                await peer.SendHaveNoneAsync(_engine.Torrent.PieceCount).ConfigureAwait(false);
                int piece = ssm.GetPieceToSuperSeed(peer, peer.PeerBitfield);
                if (piece >= 0)
                    await peer.AnnounceHaveAsync(piece).ConfigureAwait(false);
            }
            catch (Exception) { /* peer disconnected */ }

            // BEP 16: Clean up super-seed state when this peer disconnects
            peer.ConnectionLost += (_, _) =>
            {
                var ssmOnDisconnect = _engine.SuperSeedManagerInternal;
                ssmOnDisconnect?.OnPeerDisconnected(peer);
            };
        };

        // Wire progressive verification to HAVE broadcasts.
        // Only fires for pieces verified AFTER peers connect (Phase 4+).
        // Pieces verified before peer connection are covered by initial bitfield exchange on handshake.
        if (!_engine.IsVerificationComplete && _engine.PeerManagerInternal != null)
        {
            _engine.PieceVerified += pieceIndex =>
            {
                _ = _engine.PeerManagerInternal.BroadcastHaveAsync(pieceIndex).ContinueWith(
                    t => _logger.LogDebug(t.Exception?.InnerException, "HAVE broadcast failed for piece {Piece}", pieceIndex),
                    TaskContinuationOptions.OnlyOnFaulted);
            };
        }

        _logger.LogDebug("Phase 6 complete: Message handlers wired");
    }
}
