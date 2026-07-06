using System;

namespace vTorrent.Abstractions.Events;

public class TorrentErrorEventArgs : EventArgs
{
    public string InfoHash { get; }
    public string ErrorMessage { get; }

    public TorrentErrorEventArgs(string infoHash, string errorMessage)
    {
        InfoHash = infoHash;
        ErrorMessage = errorMessage;
    }
}
