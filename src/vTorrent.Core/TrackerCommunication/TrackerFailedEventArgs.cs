using System;

namespace vTorrent.Core.TrackerCommunication;

public class TrackerFailedEventArgs : EventArgs
{
    public string TrackerUrl { get; }
    public string Reason { get; }
    public Exception Exception { get; }
    public int ConsecutiveFailures { get; }
    public DateTime FailedAt { get; }

    public TrackerFailedEventArgs(string trackerUrl, string reason, Exception exception = null, int consecutiveFailures = 1)
    {
        TrackerUrl = trackerUrl;
        Reason = reason;
        Exception = exception;
        ConsecutiveFailures = consecutiveFailures;
        FailedAt = DateTime.UtcNow;
    }
}