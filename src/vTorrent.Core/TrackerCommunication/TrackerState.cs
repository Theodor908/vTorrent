using System;

namespace vTorrent.Core.TrackerCommunication;

public class TrackerState
{
    public ITrackerClient Client { get; }
    public int Tier { get; }
    public int ConsecutiveFailures { get; private set; }
    public DateTime? LastSuccess { get; private set; }
    public DateTime? LastFailure { get; private set; }

    public TrackerState(ITrackerClient client, int tier)
    {
        Client = client;
        Tier = tier;
    }

    public void RecordSuccess()
    {
        ConsecutiveFailures = 0;
        LastSuccess = DateTime.UtcNow;
    }

    public void RecordFailure()
    {
        ConsecutiveFailures++;
        LastFailure = DateTime.UtcNow;
    }
}