using System;

namespace vTorrent.Core.Orchestration.Bandwidth;

/// <summary>
/// Represents a single bandwidth request in the queue.
/// Based on libtorrent's bw_request implementation.
/// </summary>
public class BandwidthRequest
{
    /// <summary>
    /// Maximum number of bandwidth channels that can limit a single request.
    /// </summary>
    public const int MaxChannels = 10;

    /// <summary>
    /// Default TTL (time-to-live) in distribution rounds.
    /// Prevents starvation by granting bandwidth after this many rounds.
    /// </summary>
    public const int DefaultTtl = 20;

    /// <summary>
    /// The requester (typically a peer connection) waiting for bandwidth.
    /// </summary>
    public IBandwidthConsumer Consumer { get; }

    /// <summary>
    /// Priority weight (1-255). Higher priority gets proportionally more bandwidth.
    /// </summary>
    public int Priority { get; }

    /// <summary>
    /// Total bytes requested.
    /// </summary>
    public int RequestSize { get; }

    /// <summary>
    /// Bytes assigned so far.
    /// </summary>
    public int Assigned { get; private set; }

    /// <summary>
    /// Time-to-live counter. Decremented each round.
    /// When TTL reaches 0, the request is satisfied regardless of available quota.
    /// </summary>
    public int Ttl { get; private set; }

    /// <summary>
    /// Channels that limit this request.
    /// </summary>
    public BandwidthChannel?[] Channels { get; } = new BandwidthChannel?[MaxChannels];

    /// <summary>
    /// Number of active channels limiting this request.
    /// </summary>
    public int ChannelCount { get; private set; }

    /// <summary>
    /// Whether this request has been fully satisfied.
    /// </summary>
    public bool IsSatisfied => Assigned >= RequestSize;

    /// <summary>
    /// Remaining bytes needed.
    /// </summary>
    public int Remaining => RequestSize - Assigned;

    /// <summary>
    /// Creates a new bandwidth request.
    /// </summary>
    /// <param name="consumer">The bandwidth consumer (peer connection)</param>
    /// <param name="requestSize">Bytes requested</param>
    /// <param name="priority">Priority weight (1-255)</param>
    public BandwidthRequest(IBandwidthConsumer consumer, int requestSize, int priority)
    {
        Consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        RequestSize = Math.Max(0, requestSize);
        Priority = Math.Clamp(priority, 1, 255);
        Assigned = 0;
        Ttl = DefaultTtl;
        ChannelCount = 0;
    }

    /// <summary>
    /// Adds a limiting channel to this request.
    /// </summary>
    /// <param name="channel">The bandwidth channel to add</param>
    /// <returns>True if added, false if at max capacity</returns>
    public bool AddChannel(BandwidthChannel channel)
    {
        if (channel == null) return false;
        if (ChannelCount >= MaxChannels) return false;
        if (channel.IsUnlimited) return false; // Don't track unlimited channels

        Channels[ChannelCount++] = channel;
        return true;
    }

    /// <summary>
    /// Assigns bandwidth from the limiting channels.
    /// Uses priority-weighted distribution formula: (distribute_quota * priority / total_priority)
    /// </summary>
    /// <returns>Bytes assigned this round</returns>
    public int AssignBandwidth()
    {
        if (Assigned >= RequestSize)
        {
            return 0;
        }

        int quota = RequestSize - Assigned;
        --Ttl;

        if (quota == 0)
        {
            return 0;
        }

        // Find the most restrictive channel (bottleneck)
        for (int j = 0; j < ChannelCount; j++)
        {
            var channel = Channels[j];
            if (channel == null) continue;
            if (channel.Throttle == 0) continue; // Unlimited
            if (channel.TotalPriority == 0) continue; // Avoid division by zero

            // Priority-weighted distribution: distribute_quota * priority / total_priority
            int available = (int)(((long)channel.DistributeQuota * Priority) / channel.TotalPriority);
            quota = Math.Min(quota, Math.Max(0, available));
        }

        // Consume quota from all channels
        Assigned += quota;
        for (int j = 0; j < ChannelCount; j++)
        {
            var channel = Channels[j];
            channel?.UseQuota(quota);
        }

        return quota;
    }

    /// <summary>
    /// Returns any assigned but unused quota back to channels.
    /// Called when a consumer disconnects.
    /// </summary>
    public void ReturnQuota()
    {
        if (Assigned > 0)
        {
            for (int j = 0; j < ChannelCount; j++)
            {
                Channels[j]?.ReturnQuota(Assigned);
            }
        }
    }

    public override string ToString()
    {
        return $"BandwidthRequest[consumer={Consumer}, size={RequestSize}, assigned={Assigned}, priority={Priority}, ttl={Ttl}]";
    }
}

/// <summary>
/// Interface for bandwidth consumers (typically peer connections).
/// </summary>
public interface IBandwidthConsumer
{
    /// <summary>
    /// Unique identifier for this consumer.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Whether this consumer is disconnecting/disconnected.
    /// Used to clean up pending requests.
    /// </summary>
    bool IsDisconnecting { get; }

    /// <summary>
    /// Called when bandwidth is assigned to this consumer.
    /// </summary>
    /// <param name="channel">Which channel (upload/download)</param>
    /// <param name="amount">Bytes assigned</param>
    void OnBandwidthAssigned(BandwidthChannelType channel, int amount);
}

/// <summary>
/// Type of bandwidth channel.
/// </summary>
public enum BandwidthChannelType
{
    Download = 0,
    Upload = 1
}
