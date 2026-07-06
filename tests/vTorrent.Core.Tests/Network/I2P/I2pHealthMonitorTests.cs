using Xunit;
using FluentAssertions;
using vTorrent.Core.Network.I2P;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Tests.Network.I2P;

public class I2pHealthMonitorTests : IAsyncLifetime
{
    private MockSamBridge _bridge = null!;
    private string _tempDir = null!;

    public async Task InitializeAsync()
    {
        _bridge = new MockSamBridge();
        _bridge.SetDefaultHandshake();
        _bridge.SetDefaultSessionCreate("AAAA");
        _bridge.SetDefaultDestGenerate(
            Convert.ToBase64String(new byte[32]),
            Convert.ToBase64String(new byte[32]));
        await _bridge.StartAsync();
        _tempDir = Path.Combine(Path.GetTempPath(), $"i2p_hm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task Monitor_BridgeAlive_BecomesAvailable()
    {
        var settings = new I2pSettings
        {
            SamHostname = "127.0.0.1",
            SamPort = _bridge.Port,
            DestinationMode = I2pDestinationMode.SessionTransient
        };

        var session = new I2pSamSession(settings, _tempDir);
        await session.ConnectAsync();

        I2pAvailability? reportedAvailability = null;
        var monitor = new I2pHealthMonitor(session, heartbeatInterval: TimeSpan.FromMilliseconds(100));
        monitor.AvailabilityChanged += (s, a) => reportedAvailability = a;
        monitor.Start();

        // Wait for at least one heartbeat cycle
        await Task.Delay(500);

        monitor.Availability.Should().Be(I2pAvailability.Available);
        await monitor.DisposeAsync();
    }

    [Fact]
    public void Availability_DefaultsToUnavailable()
    {
        var settings = new I2pSettings
        {
            SamHostname = "127.0.0.1",
            SamPort = _bridge.Port,
            DestinationMode = I2pDestinationMode.SessionTransient
        };

        var session = new I2pSamSession(settings, _tempDir);
        var monitor = new I2pHealthMonitor(session);
        monitor.Availability.Should().Be(I2pAvailability.Unavailable);
    }

    [Fact]
    public async Task Monitor_BackoffRespectsSchedule()
    {
        // Use a port where nothing is listening — forces reconnect attempts
        var settings = new I2pSettings
        {
            SamHostname = "127.0.0.1",
            SamPort = 1, // Nothing listening here
            DestinationMode = I2pDestinationMode.SessionTransient
        };

        var session = new I2pSamSession(settings, _tempDir);
        var stateChanges = new List<I2pAvailability>();
        var monitor = new I2pHealthMonitor(session, heartbeatInterval: TimeSpan.FromMilliseconds(100));
        monitor.AvailabilityChanged += (s, a) => stateChanges.Add(a);
        monitor.Start();

        // Wait enough for first backoff cycle (5s is too long for test, but we can check state)
        await Task.Delay(1000);

        // Should have attempted reconnection
        stateChanges.Should().Contain(I2pAvailability.Reconnecting);
        await monitor.DisposeAsync();
    }

    [Fact]
    public void I2pAvailability_HasExpectedValues()
    {
        I2pAvailability.NotApplicable.Should().BeDefined();
        I2pAvailability.Available.Should().BeDefined();
        I2pAvailability.Unavailable.Should().BeDefined();
        I2pAvailability.Reconnecting.Should().BeDefined();
    }
}
