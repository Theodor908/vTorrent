using System;

namespace vTorrent.Core.TrackerCommunication;

public class ScrapeCompletedEventArgs : EventArgs
{
    public bool Success { get; }
    public int TotalSeeders { get; }
    public int TotalLeechers { get; }
    public int SuccessfulTrackers { get; }
    public int FailedTrackers { get; }
    public DateTime CompletedAt { get; }

    public ScrapeCompletedEventArgs(bool success, int totalSeeders, int totalLeechers, int successfulTrackers, int failedTrackers)
    {
        Success = success;
        TotalSeeders = totalSeeders;
        TotalLeechers = totalLeechers;
        SuccessfulTrackers = successfulTrackers;
        FailedTrackers = failedTrackers;
        CompletedAt = DateTime.UtcNow;
    }
}