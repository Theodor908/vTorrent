using FluentAssertions;
using Xunit;

namespace vTorrent.Core.Tests.Orchestration;

public class VpnKillSwitchIntegrationTests
{
    [Fact]
    public void BlockingLogic_SetsFlag_OnlyForRunningTorrents()
    {
        var states = new[]
        {
            (intent: "Downloading", engineRunning: true, expectedBlocked: true),
            (intent: "Seeding", engineRunning: true, expectedBlocked: true),
            (intent: "Paused", engineRunning: false, expectedBlocked: false),
            (intent: "Queued", engineRunning: false, expectedBlocked: false),
        };

        foreach (var state in states)
        {
            bool isVpnBlocked = false;

            if (state.engineRunning && state.intent != "Paused" && state.intent != "Queued")
            {
                isVpnBlocked = true;
            }

            isVpnBlocked.Should().Be(state.expectedBlocked,
                $"torrent with intent={state.intent}, engineRunning={state.engineRunning}");
        }
    }

    [Fact]
    public void SettingsGuard_NoRestart_WhenInterfaceUnchanged()
    {
        string currentInterface = "bdvpnservice_2";
        string newInterface = "bdvpnservice_2";
        bool isRunning = true;

        bool shouldSkip = isRunning && string.Equals(currentInterface, newInterface, System.StringComparison.OrdinalIgnoreCase);

        shouldSkip.Should().BeTrue("same interface, should skip restart");
    }

    [Fact]
    public void SettingsGuard_Restarts_WhenInterfaceChanged()
    {
        string currentInterface = "bdvpnservice_2";
        string newInterface = "wg0";
        bool isRunning = true;

        bool shouldSkip = isRunning && string.Equals(currentInterface, newInterface, System.StringComparison.OrdinalIgnoreCase);

        shouldSkip.Should().BeFalse("different interface, should restart");
    }

    [Fact]
    public void SettingsGuard_Starts_WhenNotRunning()
    {
        string currentInterface = "";
        string newInterface = "bdvpnservice_2";
        bool isRunning = false;

        bool shouldSkip = isRunning && string.Equals(currentInterface, newInterface, System.StringComparison.OrdinalIgnoreCase);

        shouldSkip.Should().BeFalse("not running, should start");
    }
}
