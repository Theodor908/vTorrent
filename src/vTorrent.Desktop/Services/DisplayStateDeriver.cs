using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Core.State;
using vTorrent.Desktop.ViewModels;

namespace vTorrent.Desktop.Services;

/// <summary>
/// Pure function: derives UI display state from orthogonal TorrentStatus + live transfer metrics.
/// Priority cascade: Error > Intent > Phase > Transfer+Stall > Idle.
///
/// Live metrics (downloadRate / uploadRate / connectedPeers) are passed explicitly because they
/// are NOT state-machine state — they are sampled from the engine's PeerManager and
/// SlidingWindowRateCalculator and live on TorrentSnapshot, not on TorrentStatus.
/// </summary>
public static class DisplayStateDeriver
{
    public static TorrentDisplayState Derive(
        TorrentStatus status,
        int downloadRate,
        int uploadRate,
        int connectedPeers)
    {
        // Priority 1 — Error states
        if (status.Error != null) return TorrentDisplayState.Error;
        if (status.MissingFiles) return TorrentDisplayState.MissingFiles;

        // Priority 2 — User intent
        if (status.Intent == UserIntent.Paused) return TorrentDisplayState.Paused;
        if (status.Intent == UserIntent.Queued) return TorrentDisplayState.Queued;

        // Priority 3 — File operations (overlay on any phase)
        if (status.FileOp == FileOperation.Moving) return TorrentDisplayState.Moving;
        if (status.FileOp == FileOperation.Rechecking) return TorrentDisplayState.Checking;

        // Priority 4 — Transitional phases
        switch (status.Phase)
        {
            case TransferPhase.Stopping: return TorrentDisplayState.Stopping;
            case TransferPhase.Allocating: return TorrentDisplayState.Allocating;
            case TransferPhase.CheckingResumeData: return TorrentDisplayState.CheckingResumeData;
            case TransferPhase.CheckingFiles: return TorrentDisplayState.Verifying;
            case TransferPhase.FetchingMetadata: return TorrentDisplayState.MetadataDownloading;
            case TransferPhase.Connecting: return TorrentDisplayState.Connecting;
        }

        // Priority 5 — Transfer activity + stall (computed from live rate/peer metrics)
        if (status.Phase == TransferPhase.Downloading)
        {
            if (downloadRate == 0 && connectedPeers == 0) return TorrentDisplayState.Stalled;
            if (!status.IsAutoManaged) return TorrentDisplayState.ForcedDownloading;
            return TorrentDisplayState.Downloading;
        }

        if (status.Phase == TransferPhase.Seeding)
        {
            if (uploadRate == 0 && connectedPeers == 0) return TorrentDisplayState.StalledSeeding;
            if (!status.IsAutoManaged) return TorrentDisplayState.ForcedSeeding;
            return TorrentDisplayState.Seeding;
        }

        // Fallback
        return TorrentDisplayState.Stopped;
    }
}
