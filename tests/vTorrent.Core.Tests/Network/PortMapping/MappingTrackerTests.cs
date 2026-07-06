using FluentAssertions;
using Xunit;
using vTorrent.Core.Network.PortMapping;
using PortMappingEntry = vTorrent.Core.Network.PortMapping.PortMapping;

namespace vTorrent.Core.Tests.Network.PortMapping;

public class MappingTrackerTests
{
    private static UpnpDevice MakeDevice() => new()
    {
        Url = "http://192.168.1.1:5000/desc.xml",
        ControlUrl = "http://192.168.1.1:5000/ctl/IPConn",
        ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1",
        Hostname = "192.168.1.1",
        Port = 5000,
        Path = "/ctl/IPConn"
    };

    private static PortMappingEntry MakeMapping(int id = 1) => new()
    {
        Id = id,
        Protocol = PortMapProtocol.Tcp,
        Transport = PortMapTransport.Upnp,
        InternalPort = 6881,
        ExternalPort = 6881,
        Expiry = DateTime.UtcNow.AddHours(1)
    };

    [Fact]
    public void InitialState_IsPending()
    {
        var tracker = new MappingTracker(MakeDevice(), MakeMapping(), 3600);
        tracker.State.Should().Be(MappingState.Pending);
        tracker.FailCount.Should().Be(0);
        tracker.ShouldRefresh.Should().BeTrue();
    }

    [Fact]
    public void RecordSuccess_TransitionsToActive()
    {
        var tracker = new MappingTracker(MakeDevice(), MakeMapping(), 3600);
        var refreshed = MakeMapping(2);
        tracker.RecordSuccess(refreshed);

        tracker.State.Should().Be(MappingState.Active);
        tracker.FailCount.Should().Be(0);
        tracker.Mapping.Id.Should().Be(2);
        tracker.ShouldRefresh.Should().BeTrue();
    }

    [Fact]
    public void RecordFailure_TransitionsToFailed()
    {
        var tracker = new MappingTracker(MakeDevice(), MakeMapping(), 3600);
        tracker.RecordFailure();

        tracker.State.Should().Be(MappingState.Failed);
        tracker.FailCount.Should().Be(1);
        tracker.ShouldRefresh.Should().BeTrue();
    }

    [Fact]
    public void RecordFailure_SixTimes_TransitionsToAbandoned()
    {
        var tracker = new MappingTracker(MakeDevice(), MakeMapping(), 3600);
        for (int i = 0; i < 6; i++)
            tracker.RecordFailure();

        tracker.State.Should().Be(MappingState.Abandoned);
        tracker.FailCount.Should().Be(6);
        tracker.ShouldRefresh.Should().BeFalse();
    }

    [Fact]
    public void RecordSuccess_ResetsFailCount()
    {
        var tracker = new MappingTracker(MakeDevice(), MakeMapping(), 3600);
        tracker.RecordFailure();
        tracker.RecordFailure();
        tracker.FailCount.Should().Be(2);

        tracker.RecordSuccess(MakeMapping());
        tracker.FailCount.Should().Be(0);
        tracker.State.Should().Be(MappingState.Active);
    }

    [Fact]
    public void FiveFailures_StillRetryable()
    {
        var tracker = new MappingTracker(MakeDevice(), MakeMapping(), 3600);
        for (int i = 0; i < 5; i++)
            tracker.RecordFailure();

        tracker.State.Should().Be(MappingState.Failed);
        tracker.ShouldRefresh.Should().BeTrue();
    }

    [Fact]
    public void Abandoned_IsTerminal()
    {
        var tracker = new MappingTracker(MakeDevice(), MakeMapping(), 3600);
        for (int i = 0; i < 6; i++)
            tracker.RecordFailure();

        tracker.State.Should().Be(MappingState.Abandoned);
        tracker.RecordSuccess(MakeMapping());
        tracker.State.Should().Be(MappingState.Abandoned);
        tracker.ShouldRefresh.Should().BeFalse();
    }
}
