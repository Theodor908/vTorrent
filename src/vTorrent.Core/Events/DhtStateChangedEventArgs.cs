namespace vTorrent.Core.Events;

/// <summary>
/// Raised when DHT state changes (initializing, running, stopped).
/// </summary>
public class DhtStateChangedEventArgs : EventArgs
{
    public bool IsRunning { get; init; }
    public bool IsInitializing { get; init; }
    public int NodeCount { get; init; }

    public DhtStateChangedEventArgs() { }

    public DhtStateChangedEventArgs(bool isRunning, bool isInitializing, int nodeCount = 0)
    {
        IsRunning = isRunning;
        IsInitializing = isInitializing;
        NodeCount = nodeCount;
    }
}
