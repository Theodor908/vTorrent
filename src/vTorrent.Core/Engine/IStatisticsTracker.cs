using System.Collections.Generic;

using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.Download;

namespace vTorrent.Core.Engine;

/// <summary>

/// Interface for tracking download/upload statistics for a torrent.

/// </summary>

public interface IStatisticsTracker

{

    // Peer registration

    void RegisterPeer(IPeerConnection peer);

    void UnregisterPeer(IPeerConnection peer);

    // Recording - total traffic (includes protocol overhead)

    void RecordDownload(IPeerConnection peer, int bytes);

    void RecordUpload(IPeerConnection peer, int bytes);

    // Recording - payload only (actual file data, no protocol overhead)

    void RecordPayloadDownload(IPeerConnection peer, int bytes);

    void RecordPayloadUpload(IPeerConnection peer, int bytes);

    void RecordPieceCompleted();

    void RecordPieceUploaded();

    void RecordFailedBytes(long bytes);

    void RecordVerifiedDownload(int bytes);

    void RecordEndgameWaste(int bytes);

    // Per-peer queries

    double GetPeerDownloadRate(IPeerConnection peer);

    double GetPeerUploadRate(IPeerConnection peer);

    long GetPeerDownloaded(IPeerConnection peer);

    long GetPeerUploaded(IPeerConnection peer);

    long GetPeerPayloadDownloaded(IPeerConnection peer);

    long GetPeerPayloadUploaded(IPeerConnection peer);

    // Global statistics - total traffic (includes protocol overhead)

    long TotalDownloaded { get; }

    long TotalUploaded { get; }

    long SessionDownloaded { get; }

    long SessionUploaded { get; }

    double DownloadRate { get; }

    double UploadRate { get; }

    // Global statistics - payload only (actual file data)

    long PayloadDownloaded { get; }

    long PayloadUploaded { get; }

    double PayloadDownloadRate { get; }

    double PayloadUploadRate { get; }

    int PiecesCompleted { get; }

    int PiecesUploaded { get; }

    long FailedBytes { get; }

    // Verified and endgame statistics

    long VerifiedDownloaded { get; }

    double VerifiedDownloadRate { get; }

    double SmoothedPayloadDownloadRate { get; }

    long EndgameWastedBytes { get; }

    int EndgameDuplicateBlocks { get; }

    int TrackedPeerCount { get; }

    // Lifecycle

    void InitializeFromExisting(long downloaded, long uploaded, int piecesCompleted);

    void ResetSession();

    void SetPaused(bool paused);

    void ResetRates();

    // Per-peer stats

    IReadOnlyDictionary<IPeerConnection, PeerTransferStats> GetAllPeerStats();

}
