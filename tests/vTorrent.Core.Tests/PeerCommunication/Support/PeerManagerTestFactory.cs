using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Interfaces.Transport;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;
using vTorrent.Core.PeerCommunication;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.Tests.PeerCommunication.Transport;

namespace vTorrent.Core.Tests.PeerCommunication.Support;

public static class PeerManagerTestFactory
{
    public static PeerManager CreateWithConnector(
        ITransportConnector connector,
        EncryptionPolicy outPolicy = EncryptionPolicy.Enabled)
    {
        var infoHash = new byte[20];
        for (int i = 0; i < 20; i++) infoHash[i] = (byte)i;
        var settings = new PeerSettings { MaxConnections = 8, ListenPort = 0 };
        var encMonitor = new StaticOptionsMonitor<EncryptionSettings>(
            new EncryptionSettings { OutPolicy = outPolicy });
        return new PeerManager(
            infoHash,
            settings,
            NullLoggerFactory.Instance,
            new PeerRegistry(),
            connector,
            encryptionMonitor: encMonitor);
    }

    public static PeerManager Create(int maxConnections)
    {
        var infoHash = new byte[20];
        for (int i = 0; i < 20; i++) infoHash[i] = (byte)i;
        var settings = new PeerSettings { MaxConnections = maxConnections, ListenPort = 0 };
        return new PeerManager(
            infoHash,
            settings,
            NullLoggerFactory.Instance,
            new PeerRegistry(),
            new StubTransportConnector());
    }

    /// <summary>
    /// Overload that shares a caller-supplied <see cref="PeerRegistry"/> (so the test can
    /// pre-populate it before/after construction) and optionally injects a live
    /// <see cref="IOptionsMonitor{ConnectionSettings}"/> to exercise the duplicate-IP guard.
    /// </summary>
    public static PeerManager Create(
        int maxConnections,
        PeerRegistry registry,
        IOptionsMonitor<ConnectionSettings>? connectionMonitor = null)
    {
        var infoHash = new byte[20];
        for (int i = 0; i < 20; i++) infoHash[i] = (byte)i;
        var settings = new PeerSettings { MaxConnections = maxConnections, ListenPort = 0 };
        return new PeerManager(
            infoHash,
            settings,
            NullLoggerFactory.Instance,
            registry ?? new PeerRegistry(),
            new StubTransportConnector(),
            connectionMonitor: connectionMonitor);
    }

    private sealed class StubTransportConnector : ITransportConnector
    {
        public Task<ITransportStream> ConnectAsync(EndPoint endpoint, CancellationToken ct = default)
            => throw new NotSupportedException("test stub");
    }
}
