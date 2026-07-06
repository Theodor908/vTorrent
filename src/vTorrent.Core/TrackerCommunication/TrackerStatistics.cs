using System;
using vTorrent.Core.TrackerCommunication.Models;

namespace vTorrent.Core.TrackerCommunication;

public class TrackerStatistics
{
    public string TrackerUrl { get; set; }
    public TrackerType Type { get; set; }
    public bool IsAvailable { get; set; }
    public int Tier { get; set; }
    
    // Announce stats
    public int TotalAnnounces { get; set; }
    public int SuccessfulAnnounces { get; set; }
    public int FailedAnnounces { get; set; }
    public int ConsecutiveFailures { get; set; }
    
    // Timing
    public DateTime? LastAnnounce { get; set; }
    public DateTime? LastSuccessfulAnnounce { get; set; }
    public DateTime? NextScheduledAnnounce { get; set; }
    public int CurrentInterval { get; set; }
    
    // Peer stats
    public int TotalPeersDiscovered { get; set; }
    public int LastPeersReceived { get; set; }
    public int LastSeeders { get; set; }
    public int LastLeechers { get; set; }
    
    // Scrape stats
    public DateTime? LastScrape { get; set; }
    public int ScrapeSeeders { get; set; }
    public int ScrapeLeechers { get; set; }
    public int ScrapeDownloaded { get; set; }
    
    // Performance
    public TimeSpan AverageResponseTime { get; set; }
    public TimeSpan LastResponseTime { get; set; }
    
    public double SuccessRate => TotalAnnounces > 0 
        ? (double)SuccessfulAnnounces / TotalAnnounces 
        : 0;

    public TrackerStatistics(string trackerUrl, TrackerType type, int tier = 0)
    {
        TrackerUrl = trackerUrl;
        Type = type;
        Tier = tier;
        IsAvailable = true;
    }

    public void RecordSuccess(int peersReceived, int seeders, int leechers, int interval, TimeSpan responseTime)
    {
        TotalAnnounces++;
        SuccessfulAnnounces++;
        ConsecutiveFailures = 0;
        
        LastAnnounce = DateTime.UtcNow;
        LastSuccessfulAnnounce = DateTime.UtcNow;
        NextScheduledAnnounce = DateTime.UtcNow.AddSeconds(interval);
        CurrentInterval = interval;
        
        LastPeersReceived = peersReceived;
        TotalPeersDiscovered += peersReceived;
        LastSeeders = seeders;
        LastLeechers = leechers;
        
        LastResponseTime = responseTime;
        UpdateAverageResponseTime(responseTime);
        
        IsAvailable = true;
    }

    public void RecordFailure(TimeSpan responseTime)
    {
        TotalAnnounces++;
        FailedAnnounces++;
        ConsecutiveFailures++;
        
        LastAnnounce = DateTime.UtcNow;
        LastResponseTime = responseTime;
        
        // Mark unavailable after too many failures
        if (ConsecutiveFailures >= 5)
        {
            IsAvailable = false;
        }
    }

    public void RecordScrape(int seeders, int leechers, int downloaded)
    {
        LastScrape = DateTime.UtcNow;
        ScrapeSeeders = seeders;
        ScrapeLeechers = leechers;
        ScrapeDownloaded = downloaded;
    }

    private void UpdateAverageResponseTime(TimeSpan newTime)
    {
        if (SuccessfulAnnounces == 1)
        {
            AverageResponseTime = newTime;
        }
        else
        {
            // Exponential moving average (weight new samples more)
            var alpha = 0.3;
            var avgMs = AverageResponseTime.TotalMilliseconds * (1 - alpha) + newTime.TotalMilliseconds * alpha;
            AverageResponseTime = TimeSpan.FromMilliseconds(avgMs);
        }
    }

    public override string ToString()
    {
        return $"TrackerStats [{TrackerUrl}] - " +
               $"Success: {SuccessRate:P0}, " +
               $"Peers: {TotalPeersDiscovered}, " +
               $"Interval: {CurrentInterval}s, " +
               $"Available: {IsAvailable}";
    }
}