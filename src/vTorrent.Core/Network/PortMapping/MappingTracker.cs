namespace vTorrent.Core.Network.PortMapping;

/// <summary>
/// Per-mapping lifecycle state. libtorrent: mapping_t::failcount + action.
/// </summary>
internal enum MappingState : byte
{
    Pending,
    Active,
    Failed,
    Abandoned
}

/// <summary>
/// Tracks per-device, per-mapping lifecycle state across refresh cycles.
/// Encapsulates the retry/abandon policy (libtorrent: failcount > 5 = give up).
/// </summary>
internal sealed class MappingTracker
{
    private const int MaxFailCount = 5;

    public UpnpDevice Device { get; }
    public PortMapping Mapping { get; private set; }
    public uint LeaseSeconds { get; }
    public MappingState State { get; private set; } = MappingState.Pending;
    public int FailCount { get; private set; }

    public bool ShouldRefresh => State is MappingState.Active or MappingState.Pending or MappingState.Failed;

    public MappingTracker(UpnpDevice device, PortMapping mapping, uint leaseSeconds)
    {
        Device = device;
        Mapping = mapping;
        LeaseSeconds = leaseSeconds;
    }

    public void RecordSuccess(PortMapping refreshed)
    {
        if (State == MappingState.Abandoned) return; // terminal state, never reverts
        Mapping = refreshed;
        FailCount = 0;
        State = MappingState.Active;
    }

    public void RecordFailure()
    {
        FailCount++;
        State = FailCount > MaxFailCount ? MappingState.Abandoned : MappingState.Failed;
    }
}
