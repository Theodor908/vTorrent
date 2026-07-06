using System;

namespace vTorrent.Core.TrackerCommunication;

public class AnnounceCompletedEventArgs : EventArgs
{
    public string TrackerUrl { get; }
    public bool Success { get; }
    public int PeersReceived { get; }
    public int Interval { get; }
    public string FailureReason { get; }
    public TimeSpan Duration { get; }
    public DateTime CompletedAt { get; }

    public AnnounceCompletedEventArgs(string trackerUrl, bool success, int peersReceived, int interval, string failureReason, TimeSpan duration)
    {
        TrackerUrl = trackerUrl;
        Success = success;
        PeersReceived = peersReceived;
        Interval = interval;
        FailureReason = failureReason;
        Duration = duration;
        CompletedAt = DateTime.UtcNow;
    }

    public static AnnounceCompletedEventArgs CreateSuccess(string trackerUrl, int peersReceived, int interval, TimeSpan duration)
    {
        return new AnnounceCompletedEventArgs(trackerUrl, true, peersReceived, interval, null, duration);
    }

    public static AnnounceCompletedEventArgs CreateFailure(string trackerUrl, string reason, TimeSpan duration)
    {
        return new AnnounceCompletedEventArgs(trackerUrl, false, 0, 0, reason, duration);
    }
}