using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Models;
using vTorrent.Core.PeerCommunication.Events;

namespace vTorrent.Core.PeerCommunication.Models;

/// <summary>
/// Aggregates two IPeerManager instances (clearnet + I2P) for mixed-mode torrents.
/// Routes operations to the appropriate inner manager based on peer type.
/// </summary>
public sealed class CompositePeerManager : IPeerManager
{
    private readonly IPeerManager _clearnet;
    private readonly IPeerManager _i2p;

    public CompositePeerManager(IPeerManager clearnet, IPeerManager i2p)
    {
        _clearnet = clearnet ?? throw new ArgumentNullException(nameof(clearnet));
        _i2p = i2p ?? throw new ArgumentNullException(nameof(i2p));

        _clearnet.PeerConnected += (s, e) => PeerConnected?.Invoke(this, e);
        _clearnet.PeerDisconnected += (s, e) => PeerDisconnected?.Invoke(this, e);
        _clearnet.MessageReceived += (s, e) => MessageReceived?.Invoke(this, e);
        _i2p.PeerConnected += (s, e) => PeerConnected?.Invoke(this, e);
        _i2p.PeerDisconnected += (s, e) => PeerDisconnected?.Invoke(this, e);
        _i2p.MessageReceived += (s, e) => MessageReceived?.Invoke(this, e);
    }

    private IPeerManager Route(PeerInfo peer) => peer.IsI2p ? _i2p : _clearnet;

    public int ConnectedPeerCount => _clearnet.ConnectedPeerCount + _i2p.ConnectedPeerCount;
    public int MaxConnections => _clearnet.MaxConnections + _i2p.MaxConnections;

    public IReadOnlyList<IPeerConnection> ConnectedPeers =>
        _clearnet.ConnectedPeers.Concat(_i2p.ConnectedPeers).ToList();

    public byte[] InfoHash => _clearnet.InfoHash;
    public long TotalBytesDownloaded => _clearnet.TotalBytesDownloaded + _i2p.TotalBytesDownloaded;
    public long TotalBytesUploaded => _clearnet.TotalBytesUploaded + _i2p.TotalBytesUploaded;

    public bool SuperSeedingActive
    {
        get => _clearnet.SuperSeedingActive;
        set { _clearnet.SuperSeedingActive = value; _i2p.SuperSeedingActive = value; }
    }

    public Task<bool> AddPeerAsync(PeerInfo peerInfo, CancellationToken cancellationToken = default)
        => Route(peerInfo).AddPeerAsync(peerInfo, cancellationToken);

    public Task AddPeersAsync(IEnumerable<PeerInfo> peers, CancellationToken cancellationToken = default)
    {
        var grouped = peers.GroupBy(p => p.IsI2p);
        var tasks = grouped.Select(g => g.Key
            ? _i2p.AddPeersAsync(g, cancellationToken)
            : _clearnet.AddPeersAsync(g, cancellationToken));
        return Task.WhenAll(tasks);
    }

    public Task RemovePeerAsync(IPeerConnection peer)
        => peer.PeerInfo.IsI2p ? _i2p.RemovePeerAsync(peer) : _clearnet.RemovePeerAsync(peer);

    public IPeerConnection GetPeer(PeerInfo peerInfo) => Route(peerInfo).GetPeer(peerInfo);
    public bool IsConnected(PeerInfo peerInfo) => Route(peerInfo).IsConnected(peerInfo);

    public void SetLocalBitfieldProvider(Func<byte[]?> provider)
    {
        _clearnet.SetLocalBitfieldProvider(provider);
        _i2p.SetLocalBitfieldProvider(provider);
    }

    public void SetHashExchangeHandler(IHashExchangeHandler? handler)
    {
        _clearnet.SetHashExchangeHandler(handler);
        _i2p.SetHashExchangeHandler(handler);
    }

    public void SetPeerExtensionSetup(Action<IPeerConnection>? callback)
    {
        _clearnet.SetPeerExtensionSetup(callback);
        _i2p.SetPeerExtensionSetup(callback);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
        => Task.WhenAll(_clearnet.StartAsync(cancellationToken), _i2p.StartAsync(cancellationToken));

    public Task StopAsync()
        => Task.WhenAll(_clearnet.StopAsync(), _i2p.StopAsync());

    public Task BroadcastHaveAsync(int pieceIndex, CancellationToken cancellationToken = default)
        => Task.WhenAll(
            _clearnet.BroadcastHaveAsync(pieceIndex, cancellationToken),
            _i2p.BroadcastHaveAsync(pieceIndex, cancellationToken));

    public Task BroadcastBitfieldAsync(byte[] bitfield, CancellationToken cancellationToken = default)
        => Task.WhenAll(
            _clearnet.BroadcastBitfieldAsync(bitfield, cancellationToken),
            _i2p.BroadcastBitfieldAsync(bitfield, cancellationToken));

    public IEnumerable<IPeerConnection> GetPeersWithPiece(int pieceIndex)
        => _clearnet.GetPeersWithPiece(pieceIndex).Concat(_i2p.GetPeersWithPiece(pieceIndex));

    public IEnumerable<IPeerConnection> GetAvailablePeers()
        => _clearnet.GetAvailablePeers().Concat(_i2p.GetAvailablePeers());

    public IEnumerable<IPeerConnection> GetInterestedPeers()
        => _clearnet.GetInterestedPeers().Concat(_i2p.GetInterestedPeers());

    public event EventHandler<PeerConnectedEventArgs>? PeerConnected;
    public event EventHandler<PeerDisconnectedEventArgs>? PeerDisconnected;
    public event EventHandler<PeerMessageEventArgs>? MessageReceived;

    public void Dispose()
    {
        _clearnet.Dispose();
        _i2p.Dispose();
    }
}
