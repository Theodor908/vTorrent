using System;
using System.Collections.Generic;

using Microsoft.Extensions.Logging;

using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Storage;

using vTorrent.Core.Download;

using vTorrent.Abstractions.Settings;

using vTorrent.Core.PeerCommunication.Models;

using vTorrent.Core.PieceIO;
using vTorrent.Core.PieceIO.Backends;

using vTorrent.Core.Session;

using vTorrent.Core.Upload;

namespace vTorrent.Core.Engine;

/// <summary>
/// Applies settings changes to a running torrent engine.
/// Extracted from TorrentEngine as part of god class decomposition (Phase 5).
/// </summary>
internal class EngineSettingsApplier
{
    private readonly Func<DownloadCoordinator?> _getDownloadCoordinator;
    private readonly Func<ChokingManager?> _getChokingManager;
    private readonly Func<IPeerManager?> _getPeerManager;
    private readonly Func<IPieceManager?> _getPieceManager;
    private readonly Func<FileProgressTracker?> _getFileProgressTracker;
    private readonly Func<TorrentStatistics?> _getTorrentStatistics;
    private readonly Func<IDiskBackend?> _getDiskBackend;
    private readonly PeerSettings _peerSettings;
    private readonly ILogger _logger;

    internal EngineSettingsApplier(
        Func<DownloadCoordinator?> getDownloadCoordinator,
        Func<ChokingManager?> getChokingManager,
        Func<IPeerManager?> getPeerManager,
        Func<IPieceManager?> getPieceManager,
        Func<FileProgressTracker?> getFileProgressTracker,
        Func<TorrentStatistics?> getTorrentStatistics,
        Func<IDiskBackend?> getDiskBackend,
        PeerSettings peerSettings,
        ILogger logger)
    {
        _getDownloadCoordinator = getDownloadCoordinator ?? throw new ArgumentNullException(nameof(getDownloadCoordinator));
        _getChokingManager = getChokingManager ?? throw new ArgumentNullException(nameof(getChokingManager));
        _getPeerManager = getPeerManager ?? throw new ArgumentNullException(nameof(getPeerManager));
        _getPieceManager = getPieceManager ?? throw new ArgumentNullException(nameof(getPieceManager));
        _getFileProgressTracker = getFileProgressTracker ?? throw new ArgumentNullException(nameof(getFileProgressTracker));
        _getTorrentStatistics = getTorrentStatistics ?? throw new ArgumentNullException(nameof(getTorrentStatistics));
        _getDiskBackend = getDiskBackend ?? throw new ArgumentNullException(nameof(getDiskBackend));
        _peerSettings = peerSettings ?? throw new ArgumentNullException(nameof(peerSettings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Apply updated settings to the running torrent engine.
    /// This allows settings changes to take effect immediately without restart.
    /// </summary>
    internal void ApplySettings(
        int? maxUploadsPerTorrent = null,
        bool? enablePex = null,
        int? unchokeIntervalSeconds = null,
        int? optimisticUnchokeIntervalSeconds = null,
        bool? closeRedundantConnections = null,
        bool? autoSequentialInSeederSwarm = null,
        bool? prioritizePartialPieces = null,
        bool? strictEndgameMode = null,
        bool? seedingOutgoingConnections = null)
    {
        var chokingManager = _getChokingManager();

        if (maxUploadsPerTorrent.HasValue && chokingManager != null)
        {
            chokingManager.Configure(
                maxSlots: maxUploadsPerTorrent.Value,
                minSlots: Math.Min(2, maxUploadsPerTorrent.Value));
            _logger.LogDebug("Applied MaxUploadsPerTorrent: {Value}", maxUploadsPerTorrent.Value);
        }

        if (enablePex.HasValue)
        {
            _peerSettings.EnablePex = enablePex.Value;
            // Note: PEX setting affects new connections; existing connections retain their state
            _logger.LogDebug("Applied EnablePex: {Value}", enablePex.Value);
        }

        if (unchokeIntervalSeconds.HasValue || optimisticUnchokeIntervalSeconds.HasValue)
        {
            chokingManager?.Configure(
                maxSlots: maxUploadsPerTorrent ?? _peerSettings.MaxUploadsPerTorrent,
                minSlots: Math.Min(2, maxUploadsPerTorrent ?? _peerSettings.MaxUploadsPerTorrent),
                rechokingInterval: unchokeIntervalSeconds.HasValue
                    ? TimeSpan.FromSeconds(unchokeIntervalSeconds.Value) : null,
                optimisticRotationInterval: optimisticUnchokeIntervalSeconds.HasValue
                    ? TimeSpan.FromSeconds(optimisticUnchokeIntervalSeconds.Value) : null);
        }

        if (closeRedundantConnections.HasValue)
        {
            (_getPeerManager() as PeerManager)?.SetCloseRedundantConnections(closeRedundantConnections.Value);
            _logger.LogDebug("Applied CloseRedundantConnections: {Value}", closeRedundantConnections.Value);
        }

        if (seedingOutgoingConnections.HasValue)
        {
            (_getPeerManager() as PeerManager)?.SetSeedingOutgoingConnections(seedingOutgoingConnections.Value);
            _logger.LogDebug("Applied SeedingOutgoingConnections: {Value}", seedingOutgoingConnections.Value);
        }

        if (prioritizePartialPieces.HasValue)
        {
            _peerSettings.PrioritizePartialPieces = prioritizePartialPieces.Value;
            _getDownloadCoordinator()?.SetPrioritizePartialPieces(prioritizePartialPieces.Value);
            _logger.LogDebug("Applied PrioritizePartialPieces: {Value}", prioritizePartialPieces.Value);
        }

        if (strictEndgameMode.HasValue)
        {
            _peerSettings.StrictEndgameMode = strictEndgameMode.Value;
            _getDownloadCoordinator()?.SetStrictEndgameMode(strictEndgameMode.Value);
            _logger.LogDebug("Applied StrictEndgameMode: {Value}", strictEndgameMode.Value);
        }
    }

    /// <summary>
    /// Set priority for a specific file.
    /// </summary>
    internal void SetFilePriority(int fileIndex, FilePriority priority)
    {
        var fileProgressTracker = _getFileProgressTracker();
        if (fileProgressTracker == null)
            throw new InvalidOperationException("Engine not initialized");

        fileProgressTracker.SetFilePriority(fileIndex, (int)priority);
        _logger.LogDebug("Set file {Index} priority to {Priority}", fileIndex, priority);

        // Notify partfile-aware backend of priority change
        if (_getDiskBackend() is PartFileAwareDiskBackend partFileBackend)
            Task.Run(() => partFileBackend.OnSingleFilePriorityChangedAsync(fileIndex, priority))
                .GetAwaiter().GetResult();

        var torrentStatistics = _getTorrentStatistics();
        if (torrentStatistics != null)
        {
            torrentStatistics.TotalWanted = fileProgressTracker.GetTotalWantedBytes();
            torrentStatistics.TotalWantedDone = fileProgressTracker.GetWantedBytesCompleted();
        }
    }

    /// <summary>
    /// Set all file priorities from an array (one entry per file).
    /// Also updates the DownloadCoordinator's piece picker and statistics.
    /// </summary>
    internal void SetAllFilePriorities(FilePriority[] priorities)
    {
        var fileProgressTracker = _getFileProgressTracker();
        if (fileProgressTracker == null)
            throw new InvalidOperationException("Engine not initialized");

        _getDownloadCoordinator()?.SetFilePriorities(priorities);

        // Notify partfile-aware backend of priority changes
        if (_getDiskBackend() is PartFileAwareDiskBackend partFileBackend)
            Task.Run(() => partFileBackend.OnFilePrioritiesChangedAsync(priorities))
                .GetAwaiter().GetResult();

        var torrentStatistics = _getTorrentStatistics();
        if (torrentStatistics != null)
        {
            torrentStatistics.TotalWanted = fileProgressTracker.GetTotalWantedBytes();
            torrentStatistics.TotalWantedDone = fileProgressTracker.GetWantedBytesCompleted();
        }
    }

    /// <summary>
    /// Set priority for multiple files.
    /// </summary>
    internal void SetFilePriorities(IEnumerable<(int fileIndex, FilePriority priority)> priorities)
    {
        var fileProgressTracker = _getFileProgressTracker();
        if (fileProgressTracker == null)
            throw new InvalidOperationException("Engine not initialized");

        var partFileBackend = _getDiskBackend() as PartFileAwareDiskBackend;

        foreach (var (fileIndex, priority) in priorities)
        {
            fileProgressTracker.SetFilePriority(fileIndex, (int)priority);

            if (partFileBackend != null)
                Task.Run(() => partFileBackend.OnSingleFilePriorityChangedAsync(fileIndex, priority))
                    .GetAwaiter().GetResult();
        }

        _logger.LogDebug("Updated priorities for multiple files");

        var torrentStatistics = _getTorrentStatistics();
        if (torrentStatistics != null)
        {
            torrentStatistics.TotalWanted = fileProgressTracker.GetTotalWantedBytes();
            torrentStatistics.TotalWantedDone = fileProgressTracker.GetWantedBytesCompleted();
        }
    }

    /// <summary>
    /// Enable or disable sequential download mode.
    /// </summary>
    internal void SetSequentialDownload(bool enabled)
    {
        _getDownloadCoordinator()?.SetSequentialMode(enabled);
        _logger.LogDebug("Sequential download mode {State}", enabled ? "enabled" : "disabled");
    }

    /// <summary>
    /// Enable or disable first/last piece priority for each file.
    /// </summary>
    internal void SetFirstLastPiecePriority(bool enabled)
    {
        _getDownloadCoordinator()?.SetFirstLastPiecePriority(enabled);
        _logger.LogDebug("First/last piece priority {State}", enabled ? "enabled" : "disabled");
    }

    /// <summary>
    /// Set a deadline for a specific piece (streaming API).
    /// </summary>
    internal void SetPieceDeadline(int pieceIndex, int deadlineMs, bool alertWhenAvailable = false)
    {
        _getDownloadCoordinator()?.SetPieceDeadline(pieceIndex, deadlineMs, alertWhenAvailable);
    }

    /// <summary>Remove deadline from a specific piece.</summary>
    internal void ResetPieceDeadline(int pieceIndex)
    {
        _getDownloadCoordinator()?.ResetPieceDeadline(pieceIndex);
    }

    /// <summary>Remove all piece deadlines.</summary>
    internal void ClearPieceDeadlines()
    {
        _getDownloadCoordinator()?.ClearPieceDeadlines();
    }
}
