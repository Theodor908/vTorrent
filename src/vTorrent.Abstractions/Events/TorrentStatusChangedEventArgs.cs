using System;
using vTorrent.Abstractions.Models;

namespace vTorrent.Abstractions.Events;

/// <summary>
/// Raised when a torrent's orthogonal status changes.
/// Used by ITorrentService for external consumers (Server, Desktop, CLI).
/// </summary>
public class TorrentStatusChangedEventArgs : EventArgs
{
    public string InfoHash { get; }
    public string Name { get; }
    public TorrentStatus OldStatus { get; }
    public TorrentStatus NewStatus { get; }

    public TorrentStatusChangedEventArgs(
        string infoHash, string name,
        TorrentStatus oldStatus, TorrentStatus newStatus)
    {
        InfoHash = infoHash;
        Name = name;
        OldStatus = oldStatus;
        NewStatus = newStatus;
    }

    public bool PhaseChanged  => OldStatus.Phase  != NewStatus.Phase;
    public bool IntentChanged => OldStatus.Intent != NewStatus.Intent;
    public bool ErrorChanged   => OldStatus.Error   != NewStatus.Error;
    public bool FileOpChanged => OldStatus.FileOp != NewStatus.FileOp;
}
