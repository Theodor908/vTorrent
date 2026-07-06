using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.DHT;
using Xunit;

namespace vTorrent.Core.Tests.DHT;

public class DhtManagerDualNodeTests
{
    /// <summary>
    /// Creates a settings monitor with DHT disabled and empty bootstrap nodes
    /// so that DhtNode.StartAsync won't attempt DNS resolution.
    /// </summary>
    private static IOptionsMonitor<DhtSettings> CreateSettingsMonitor(bool enabled = false)
    {
        var settings = new DhtSettings
        {
            Enabled = enabled,
            BootstrapNodes = Array.Empty<string>(),
            I2pBootstrapNodes = Array.Empty<string>(),
        };
        var mock = new Mock<IOptionsMonitor<DhtSettings>>();
        mock.Setup(m => m.CurrentValue).Returns(settings);
        return mock.Object;
    }

    private static Mock<IDhtTransport> CreateMockTransport()
    {
        var mock = new Mock<IDhtTransport>();
        mock.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mock.Setup(m => m.CompactNodeInfoSize).Returns(26); // IPv4 compact node info size
        mock.Setup(m => m.EncodeCompactNodeInfo(It.IsAny<object>())).Returns(Array.Empty<byte>());
        return mock;
    }

    [Fact]
    public async Task StartI2pNodeAsync_CalledTwice_DoesNotCreateSecondNode()
    {
        // Arrange
        var monitor = CreateSettingsMonitor(enabled: false);
        var clearnetTransport = CreateMockTransport();
        var manager = new DhtManager(monitor, clearnetTransport.Object);

        var i2pTransport1 = CreateMockTransport();
        var i2pTransport2 = CreateMockTransport();

        // Act — start I2P node once, then call again (should be no-op)
        await manager.StartI2pNodeAsync(i2pTransport1.Object, CancellationToken.None);
        await manager.StartI2pNodeAsync(i2pTransport2.Object, CancellationToken.None);

        // Assert — first transport was started, second was never touched
        i2pTransport1.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        i2pTransport2.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void StopI2pNode_WhenNoI2pNode_DoesNotThrow()
    {
        // Arrange
        var monitor = CreateSettingsMonitor(enabled: false);
        var transport = CreateMockTransport();
        var manager = new DhtManager(monitor, transport.Object);

        // Act & Assert — calling StopI2pNode without a prior StartI2pNodeAsync must be a safe no-op
        var act = () => manager.StopI2pNode();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task StopI2pNode_DisposesI2pTransport()
    {
        // Arrange
        var monitor = CreateSettingsMonitor(enabled: false);
        var clearnetTransport = CreateMockTransport();
        var manager = new DhtManager(monitor, clearnetTransport.Object);

        var i2pTransport = CreateMockTransport();
        await manager.StartI2pNodeAsync(i2pTransport.Object, CancellationToken.None);

        // Act
        manager.StopI2pNode();

        // Assert — the I2P transport must be disposed at least once.
        // DhtNode.Dispose() disposes the transport it owns, and StopI2pNode
        // also calls _i2pTransport.Dispose() for belt-and-suspenders cleanup —
        // so the real count is 2, but the contract being tested is that disposal
        // always happens when StopI2pNode is called.
        i2pTransport.Verify(m => m.Dispose(), Times.AtLeastOnce());
    }
}
