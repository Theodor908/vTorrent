using vTorrent.Abstractions.Models;

namespace vTorrent.Core.Events;

/// <summary>
/// Base class for orchestrator-level torrent events that carry an InfoHash.
/// </summary>
public class TorrentEventArgs : EventArgs
{
    public string InfoHash { get; init; } = "";
    public string Name { get; init; } = "";

    public TorrentEventArgs() { }

    public TorrentEventArgs(string infoHash, string name)
    {
        InfoHash = infoHash;
        Name = name;
    }
}

/// <summary>
/// Raised when a torrent's orthogonal status changes.
/// </summary>
public class TorrentStatusChangedEventArgs : TorrentEventArgs
{
    public TorrentStatus OldStatus { get; init; }
    public TorrentStatus NewStatus { get; init; }

    public TorrentStatusChangedEventArgs() { }

    public TorrentStatusChangedEventArgs(
        string infoHash, string name,
        TorrentStatus oldStatus, TorrentStatus newStatus)
        : base(infoHash, name)
    {
        OldStatus = oldStatus;
        NewStatus = newStatus;
    }

    public bool PhaseChanged  => OldStatus.Phase  != NewStatus.Phase;
    public bool IntentChanged => OldStatus.Intent != NewStatus.Intent;
    public bool ErrorChanged  => OldStatus.Error   != NewStatus.Error;
    public bool FileOpChanged => OldStatus.FileOp != NewStatus.FileOp;
}

/// <summary>
/// Raised when a torrent is added to the orchestrator.
/// </summary>
public class TorrentAddedEventArgs : TorrentEventArgs
{
    public TorrentAddedEventArgs() { }
    public TorrentAddedEventArgs(string infoHash, string name) : base(infoHash, name) { }
}

/// <summary>
/// Raised when a torrent is removed from the orchestrator.
/// </summary>
public class TorrentRemovedEventArgs : TorrentEventArgs
{
    public bool DeleteFiles { get; init; }

    public TorrentRemovedEventArgs() { }

    public TorrentRemovedEventArgs(string infoHash, string name, bool deleteFiles) : base(infoHash, name)
    {
        DeleteFiles = deleteFiles;
    }
}

/// <summary>
/// Raised when a torrent completes downloading.
/// </summary>
public class TorrentCompletedEventArgs : TorrentEventArgs
{
    public TorrentCompletedEventArgs() { }
    public TorrentCompletedEventArgs(string infoHash, string name) : base(infoHash, name) { }
}

/// <summary>
/// Raised when a torrent encounters an error.
/// </summary>
public class TorrentFailedEventArgs : TorrentEventArgs
{
    public string Error { get; init; } = "";

    public TorrentFailedEventArgs() { }

    public TorrentFailedEventArgs(string infoHash, string name, string error) : base(infoHash, name)
    {
        Error = error;
    }
}
