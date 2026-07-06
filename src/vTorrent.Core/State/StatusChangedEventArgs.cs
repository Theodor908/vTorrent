using System;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.State;

public class StatusChangedEventArgs : EventArgs
{
    public TorrentStatus OldStatus { get; }
    public TorrentStatus NewStatus { get; }

    public StatusChangedEventArgs(TorrentStatus oldStatus, TorrentStatus newStatus)
    {
        OldStatus = oldStatus;
        NewStatus = newStatus;
    }

    // Convenience — which dimensions actually changed?
    public bool PhaseChanged   => OldStatus.Phase  != NewStatus.Phase;
    public bool FileOpChanged  => OldStatus.FileOp != NewStatus.FileOp;
    public bool IntentChanged  => OldStatus.Intent != NewStatus.Intent;
    public bool ErrorChanged   => OldStatus.Error   != NewStatus.Error;
}
