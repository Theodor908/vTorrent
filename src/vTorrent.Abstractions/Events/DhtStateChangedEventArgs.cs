using System;

namespace vTorrent.Abstractions.Events;

public class DhtStateChangedEventArgs : EventArgs
{
    public bool IsRunning { get; }
    public int NodeCount { get; }

    public DhtStateChangedEventArgs(bool isRunning, int nodeCount)
    {
        IsRunning = isRunning;
        NodeCount = nodeCount;
    }
}
