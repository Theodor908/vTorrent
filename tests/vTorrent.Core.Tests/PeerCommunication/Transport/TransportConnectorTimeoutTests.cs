using FluentAssertions;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.PeerCommunication.Transport;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication.Transport;

/// <summary>
/// Locks in that the uTP connect timeout is honoured from PeerSettings rather
/// than hardcoded. Regression guard for the dead-setting bug where
/// PeerSettings.UtpConnectTimeoutMs flowed into UtpTuning but was never read,
/// so changing it had no effect on the connect path.
/// </summary>
public class TransportConnectorTimeoutTests
{
    private const int DefaultUtpConnectTimeoutMs = 5_000;

    [Fact]
    public void UtpConnectTimeout_HonoursConfiguredSetting()
    {
        var settings = new PeerSettings { UtpConnectTimeoutMs = 12_345 };
        var connector = new TransportConnector(utpManager: null, settings);

        connector.UtpConnectTimeoutMs.Should().Be(12_345);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UtpConnectTimeout_FallsBackToDefault_OnNonPositiveSetting(int configured)
    {
        var settings = new PeerSettings { UtpConnectTimeoutMs = configured };
        var connector = new TransportConnector(utpManager: null, settings);

        connector.UtpConnectTimeoutMs.Should().Be(DefaultUtpConnectTimeoutMs);
    }
}
