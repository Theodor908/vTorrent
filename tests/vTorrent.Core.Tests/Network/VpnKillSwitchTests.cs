using System;
using FluentAssertions;
using vTorrent.Core.Network;
using Xunit;

namespace vTorrent.Core.Tests.Network;

public class VpnKillSwitchTests
{
    [Fact]
    public void Stop_DoesNotFireBlockingStateChanged()
    {
        var killSwitch = new VpnKillSwitch();
        bool eventFired = false;
        killSwitch.BlockingStateChanged += (blocking) => eventFired = true;

        // Start monitoring a non-existent interface (will set _isVpnUp = false, _isBlocking = true)
        killSwitch.Start("non_existent_interface_xyz");

        // Stop should NOT fire BlockingStateChanged
        eventFired = false;
        killSwitch.Stop();

        eventFired.Should().BeFalse("Stop() should silently clean up without firing BlockingStateChanged");
    }

    [Fact]
    public void Stop_PreservesBlockingState()
    {
        var killSwitch = new VpnKillSwitch();

        killSwitch.Start("non_existent_interface_xyz");
        killSwitch.IsBlocking.Should().BeTrue("interface doesn't exist, should be blocking");

        killSwitch.Stop();

        killSwitch.IsBlocking.Should().BeTrue("Stop() should not clear blocking state");
    }

    [Fact]
    public void Stop_CleansUpResources()
    {
        var killSwitch = new VpnKillSwitch();
        killSwitch.Start("non_existent_interface_xyz");

        killSwitch.Stop();

        killSwitch.IsMonitoring.Should().BeFalse("Stop() should set IsMonitoring to false");
    }

    [Fact]
    public void StopThenStart_WithInterfaceDown_PreservesBlockingState()
    {
        var killSwitch = new VpnKillSwitch();
        bool? lastBlockingState = null;
        killSwitch.BlockingStateChanged += (blocking) => lastBlockingState = blocking;

        killSwitch.Start("non_existent_interface_xyz");
        lastBlockingState.Should().Be(true);

        lastBlockingState = null;
        killSwitch.Stop();
        lastBlockingState.Should().BeNull("Stop should not fire event");

        lastBlockingState = null;
        killSwitch.Start("non_existent_interface_xyz");
        lastBlockingState.Should().BeNull("no state transition when interface was already down");
        killSwitch.IsBlocking.Should().BeTrue();
    }
}
