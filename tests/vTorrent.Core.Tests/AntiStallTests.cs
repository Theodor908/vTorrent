using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Moq;
using vTorrent.Core;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Tests.Mocks;

namespace vTorrent.Tests.Unit.Core;

public class AntiStallTests
{
    [Fact]
    public void PipelineDepth_ReqqPropertyAccessible()
    {
        var mock = MockFactories.CreatePeerConnectionMock(hasPieces: true);
        mock.Setup(p => p.RemoteRequestQueueSize).Returns(50);
        mock.Setup(p => p.BytesDownloaded).Returns(1_000_000);

        Assert.Equal(50, mock.Object.RemoteRequestQueueSize);
    }
}
