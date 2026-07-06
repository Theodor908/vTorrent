using System;
using System.Collections.Generic;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Models;
using vTorrent.Core.State;

namespace vTorrent.Core.Orchestration.Alerts;

// ── Status Alerts ───────────────────────────────────────────

/// <summary>
/// Torrent status changed (dimensional: Phase, Intent, Health)
/// </summary>
public class TorrentStatusChangedAlert : Alert
{
    public override string? InfoHash { get; }
    public TorrentStatus OldStatus { get; }
    public TorrentStatus NewStatus { get; }

    public override AlertCategory Category => AlertCategory.Status;
    public override AlertPriority Priority => AlertPriority.Normal;
    public override string Message => $"Status: {OldStatus.Phase} ({OldStatus.Intent}) -> {NewStatus.Phase} ({NewStatus.Intent})";

    public TorrentStatusChangedAlert(string infoHash, TorrentStatus oldStatus, TorrentStatus newStatus)
    {
        InfoHash = infoHash;
        OldStatus = oldStatus;
        NewStatus = newStatus;
    }
}

/// <summary>
/// Torrent finished downloading
/// </summary>
public class TorrentFinishedAlert : Alert
{
    public override string? InfoHash { get; }
    public string Name { get; }

    public override AlertCategory Category => AlertCategory.Status;
    public override AlertPriority Priority => AlertPriority.High;
    public override string Message => $"Torrent finished: {Name}";

    public TorrentFinishedAlert(string infoHash, string name)
    {
        InfoHash = infoHash;
        Name = name;
    }
}

/// <summary>
/// Torrent paused
/// </summary>
public class TorrentPausedAlert : Alert
{
    public override string? InfoHash { get; }
    public bool UserInitiated { get; }

    public override AlertCategory Category => AlertCategory.Status;
    public override AlertPriority Priority => AlertPriority.Normal;
    public override string Message => UserInitiated
        ? "Torrent paused by user"
        : "Torrent paused by auto-manager";

    public TorrentPausedAlert(string infoHash, bool userInitiated)
    {
        InfoHash = infoHash;
        UserInitiated = userInitiated;
    }
}

/// <summary>
/// Torrent resumed
/// </summary>
public class TorrentResumedAlert : Alert
{
    public override string? InfoHash { get; }

    public override AlertCategory Category => AlertCategory.Status;
    public override AlertPriority Priority => AlertPriority.Normal;
    public override string Message => "Torrent resumed";

    public TorrentResumedAlert(string infoHash)
    {
        InfoHash = infoHash;
    }
}

// ── Error Alerts ────────────────────────────────────────────

/// <summary>
/// General torrent error
/// </summary>
public class TorrentErrorAlert : Alert
{
    public override string? InfoHash { get; }
    public string Error { get; }
    public TorrentErrorType ErrorType { get; }

    public override AlertCategory Category => AlertCategory.Error;
    public override AlertPriority Priority => AlertPriority.High;
    public override string Message => $"Error: {Error}";

    public TorrentErrorAlert(string infoHash, string error, TorrentErrorType errorType = TorrentErrorType.General)
    {
        InfoHash = infoHash;
        Error = error;
        ErrorType = errorType;
    }
}

/// <summary>
/// Types of torrent errors
/// </summary>
public enum TorrentErrorType
{
    General,
    Disk,
    Network,
    Tracker,
    Protocol,
    Metadata
}

// ── Tracker Alerts ──────────────────────────────────────────

/// <summary>
/// Tracker announce succeeded
/// </summary>
public class TrackerAnnounceAlert : Alert
{
    public override string? InfoHash { get; }
    public string TrackerUrl { get; }
    public int Seeders { get; }
    public int Leechers { get; }
    public int PeersReceived { get; }

    public override AlertCategory Category => AlertCategory.Tracker;
    public override AlertPriority Priority => AlertPriority.Normal;
    public override string Message => $"Announce: {Seeders}S/{Leechers}L, {PeersReceived} peers from {TrackerUrl}";

    public TrackerAnnounceAlert(string infoHash, string trackerUrl, int seeders, int leechers, int peersReceived)
    {
        InfoHash = infoHash;
        TrackerUrl = trackerUrl;
        Seeders = seeders;
        Leechers = leechers;
        PeersReceived = peersReceived;
    }
}

// ── Progress Alerts ─────────────────────────────────────────

/// <summary>
/// Piece verification completed
/// </summary>
public class CheckingCompleteAlert : Alert
{
    public override string? InfoHash { get; }
    public int GoodPieces { get; }
    public int BadPieces { get; }
    public int InconsistentPieces { get; }
    public int TotalPieces { get; }

    public override AlertCategory Category => AlertCategory.Progress;
    public override AlertPriority Priority => InconsistentPieces > 0 ? AlertPriority.High : AlertPriority.Normal;
    public override string Message
    {
        get
        {
            var msg = BadPieces > 0
                ? $"Check complete: {GoodPieces}/{TotalPieces} good, {BadPieces} bad"
                : $"Check complete: {GoodPieces}/{TotalPieces} good";
            if (InconsistentPieces > 0)
                msg += $", {InconsistentPieces} inconsistent (V1/V2 mismatch)";
            return msg;
        }
    }

    public CheckingCompleteAlert(string infoHash, int goodPieces, int badPieces, int totalPieces, int inconsistentPieces = 0)
    {
        InfoHash = infoHash;
        GoodPieces = goodPieces;
        BadPieces = badPieces;
        TotalPieces = totalPieces;
        InconsistentPieces = inconsistentPieces;
    }
}

/// <summary>
/// Files on disk are larger than torrent metadata expects.
/// </summary>
public class OversizedFilesAlert : Alert
{
    public override string? InfoHash { get; }
    public IReadOnlyList<(string Path, long Expected, long Actual)> Files { get; }

    public override AlertCategory Category => AlertCategory.Progress;
    public override AlertPriority Priority => AlertPriority.Normal;
    public override string Message => $"{Files.Count} file(s) larger than expected";

    public OversizedFilesAlert(string infoHash, IReadOnlyList<(string Path, long Expected, long Actual)> files)
    {
        InfoHash = infoHash;
        Files = files;
    }
}
