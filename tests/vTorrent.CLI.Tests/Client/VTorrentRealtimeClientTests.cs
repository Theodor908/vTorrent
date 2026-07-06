using FluentAssertions;
using Xunit;
using vTorrent.Cli.Client;

namespace vTorrent.Cli.Tests.Client;

public class VTorrentRealtimeClientTests
{
    [Fact]
    public void ConnectionStateChanged_Event_Exists()
    {
        var eventInfo = typeof(VTorrentRealtimeClient)
            .GetEvent("ConnectionStateChanged");
        eventInfo.Should().NotBeNull(
            "VTorrentRealtimeClient should expose a ConnectionStateChanged event");
    }

    [Fact]
    public void RealtimeConnectionState_HasExpectedValues()
    {
        var values = System.Enum.GetNames(typeof(RealtimeConnectionState));
        values.Should().Contain("Connecting");
        values.Should().Contain("Connected");
        values.Should().Contain("Reconnecting");
        values.Should().Contain("Disconnected");
    }
}
