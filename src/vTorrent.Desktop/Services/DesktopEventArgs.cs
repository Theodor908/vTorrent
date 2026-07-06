using System;
using System.Collections.Generic;
using vTorrent.Abstractions.Models;
using vTorrent.Desktop.ViewModels;

namespace vTorrent.Desktop.Services;

/// <summary>
/// EventArgs for events that carry a TorrentViewModel payload.
/// Used by TorrentAdded, TorrentUpdated, and TorrentCompleted events.
/// </summary>
public class TorrentViewModelEventArgs : EventArgs
{
    public TorrentViewModel Torrent { get; }

    public TorrentViewModelEventArgs(TorrentViewModel torrent)
    {
        Torrent = torrent;
    }
}

/// <summary>
/// EventArgs for the TorrentRemoved event (carries info hash).
/// </summary>
public class TorrentRemovedEventArgs : EventArgs
{
    public string InfoHash { get; }

    public TorrentRemovedEventArgs(string infoHash)
    {
        InfoHash = infoHash;
    }
}

/// <summary>
/// EventArgs for the StatsUpdated event.
/// </summary>
public class StatsUpdatedEventArgs : EventArgs
{
    public SessionStatistics Statistics { get; }
    public IReadOnlyList<TorrentViewModel> Torrents { get; }

    public StatsUpdatedEventArgs(SessionStatistics statistics, IReadOnlyList<TorrentViewModel> torrents)
    {
        Statistics = statistics;
        Torrents = torrents;
    }
}

/// <summary>
/// EventArgs for the DhtStateChanged event on the Desktop service layer.
/// </summary>
public class DesktopDhtStateChangedEventArgs : EventArgs
{
    public bool IsRunning { get; }
    public bool IsInitializing { get; }
    public int NodeCount { get; }

    public DesktopDhtStateChangedEventArgs(bool isRunning, bool isInitializing, int nodeCount)
    {
        IsRunning = isRunning;
        IsInitializing = isInitializing;
        NodeCount = nodeCount;
    }
}
