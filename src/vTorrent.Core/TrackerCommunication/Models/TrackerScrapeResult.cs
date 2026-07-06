using System;
using System.Collections.Generic;

namespace vTorrent.Core.TrackerCommunication.Models;

public class TrackerScrapeResult
{
    public bool IsSuccess { get; set; }
    
    public Dictionary<string, ScrapeResponse> TrackerResponses { get; set; }
    
    public int SuccessfulTrackers { get; set; }

    public int FailedTrackers { get; set; }

    public int TotalSeeders { get; set; }

    public int TotalLeechers { get; set; }

    public int TotalDownloaded { get; set; }

    public DateTime CompletedAt { get; set; }

    public List<string> Errors { get; set; }

    public TrackerScrapeResult()
    {
        TrackerResponses = new Dictionary<string, ScrapeResponse>();
        Errors = new List<string>();
        CompletedAt = DateTime.UtcNow;
    }

    public static TrackerScrapeResult FromResponses(Dictionary<string, ScrapeResponse> responses)
    {
        var result = new TrackerScrapeResult
        {
            TrackerResponses = responses
        };

        int successCount = 0;
        int failCount = 0;
        int totalSeeders = 0;
        int totalLeechers = 0;
        int totalDownloaded = 0;

        foreach (var (trackerUrl, response) in responses)
        {
            if (response.IsSuccess)
            {
                successCount++;
                totalSeeders += response.Complete;
                totalLeechers += response.Incomplete;
                totalDownloaded += response.Downloaded;
            }
            else
            {
                failCount++;
                result.Errors.Add($"{trackerUrl}: {response.ErrorMessage}");
            }
        }

        result.IsSuccess = successCount > 0;
        result.SuccessfulTrackers = successCount;
        result.FailedTrackers = failCount;
        result.TotalSeeders = totalSeeders;
        result.TotalLeechers = totalLeechers;
        result.TotalDownloaded = totalDownloaded;

        return result;
    }

    public static TrackerScrapeResult CreateFailure(string errorMessage)
    {
        return new TrackerScrapeResult
        {
            IsSuccess = false,
            FailedTrackers = 1,
            Errors = new List<string> { errorMessage }
        };
    }

    public override string ToString()
    {
        if (!IsSuccess)
            return $"TrackerScrapeResult [Failed: {string.Join("; ", Errors)}]";

        return $"TrackerScrapeResult [Success: {SuccessfulTrackers}/{SuccessfulTrackers + FailedTrackers} trackers, " +
               $"Seeders: {TotalSeeders}, Leechers: {TotalLeechers}, Downloads: {TotalDownloaded}]";
    }
}