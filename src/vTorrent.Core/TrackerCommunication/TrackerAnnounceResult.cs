using System;
using System.Collections.Generic;
using vTorrent.Core.TrackerCommunication.Models;

namespace vTorrent.Core.TrackerCommunication;

public class TrackerAnnounceResult
{
        public bool IsSuccess { get; set; }

    public List<TrackerPeer> Peers { get; set; }

    public int SuccessfulTrackers { get; set; }

    public int FailedTrackers { get; set; }

    public int TotalSeeders { get; set; }

    public int TotalLeechers { get; set; }

    public int RecommendedInterval { get; set; }

    public DateTime CompletedAt { get; set; }

    public List<string> Errors { get; set; }

    public Dictionary<string, TrackerResponse> TrackerResponses { get; set; }

    public TrackerAnnounceResult()
    {
        Peers = new List<TrackerPeer>();
        Errors = new List<string>();
        TrackerResponses = new Dictionary<string, TrackerResponse>();
        CompletedAt = DateTime.UtcNow;
    }

    public static TrackerAnnounceResult FromResponses(Dictionary<string, TrackerResponse> responses)
    {
        var result = new TrackerAnnounceResult
        {
            TrackerResponses = responses
        };

        var allPeers = new HashSet<TrackerPeer>();
        int maxSeeders = 0;
        int maxLeechers = 0;
        int minInterval = int.MaxValue;
        int successCount = 0;
        int failCount = 0;

        foreach (var (trackerUrl, response) in responses)
        {
            if (response.IsSuccess)
            {
                successCount++;
                
                // Collect unique peers
                foreach (var peer in response.Peers)
                {
                    allPeers.Add(peer);
                }
                
                // Track max seeders/leechers
                maxSeeders = Math.Max(maxSeeders, response.Complete);
                maxLeechers = Math.Max(maxLeechers, response.Incomplete);
                
                // Track min interval
                if (response.Interval > 0)
                {
                    minInterval = Math.Min(minInterval, response.Interval);
                }
            }
            else
            {
                failCount++;
                result.Errors.Add($"{trackerUrl}: {response.FailureReason}");
            }
        }

        result.IsSuccess = successCount > 0;
        result.Peers = new List<TrackerPeer>(allPeers);
        result.SuccessfulTrackers = successCount;
        result.FailedTrackers = failCount;
        result.TotalSeeders = maxSeeders;
        result.TotalLeechers = maxLeechers;
        result.RecommendedInterval = minInterval == int.MaxValue ? 1800 : minInterval;

        return result;
    }

    public static TrackerAnnounceResult CreateFailure(string errorMessage)
    {
        return new TrackerAnnounceResult
        {
            IsSuccess = false,
            FailedTrackers = 1,
            Errors = new List<string> { errorMessage }
        };
    }

    public override string ToString()
    {
        if (!IsSuccess)
            return $"TrackerAnnounceResult [Failed: {string.Join("; ", Errors)}]";

        return $"TrackerAnnounceResult [Success: {SuccessfulTrackers}/{SuccessfulTrackers + FailedTrackers} trackers, " +
               $"Peers: {Peers.Count}, Seeders: {TotalSeeders}, Leechers: {TotalLeechers}, " +
               $"Interval: {RecommendedInterval}s]";
    }
}