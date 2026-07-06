using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Models;
using vTorrent.Bench.Config;
using vTorrent.Core.PeerCommunication.Events;
using vTorrent.Core.PeerCommunication.Models;

namespace vTorrent.Bench.Simulation;

/// <summary>
/// A fake IPeerManager that manages a fixed pool of SyntheticPeers created
/// from a ScenarioConfig. Used to wire DownloadCoordinator in bench mode.
/// </summary>
public sealed class FakePeerManager : IPeerManager
{
    private readonly SyntheticPeer[] _peers;
    private bool _disposed;

    public FakePeerManager(ScenarioConfig config, SyntheticTorrent torrent)
    {
        _peers = new SyntheticPeer[config.PeerCount];
        for (int i = 0; i < config.PeerCount; i++)
        {
            var peer = new SyntheticPeer(
                id: i,
                torrent: torrent,
                maxUploadRate: config.MaxUploadRatePerPeer,
                roundTripTimeMs: config.RoundTripTimeMs,
                chokeProbability: config.ChokeProbability,
                chokeIntervalSec: config.ChokeIntervalSec,
                packetLossPercent: config.PacketLossPercent,
                bitfieldFill: config.PeerBitfieldFill,
                bandwidthFluctuation: config.BandwidthFluctuation,
                fluctuationAmplitude: config.FluctuationAmplitude);

            // Subscribe: relay PeerMessageReceivedEventArgs -> PeerMessageEventArgs
            peer.MessageReceived += OnPeerMessageReceived;

            _peers[i] = peer;
        }

        InfoHash = new byte[20]; // synthetic torrent has no real info-hash
    }

    // ------------------------------------------------------------------
    // IPeerManager properties
    // ------------------------------------------------------------------

    public int ConnectedPeerCount => _peers.Length;
    public int MaxConnections => _peers.Length;
    public IReadOnlyList<IPeerConnection> ConnectedPeers => _peers;
    public byte[] InfoHash { get; }
    public long TotalBytesDownloaded => 0;
    public long TotalBytesUploaded => 0;
    public bool SuperSeedingActive { get; set; }

    // ------------------------------------------------------------------
    // IPeerManager events
    // ------------------------------------------------------------------

    public event EventHandler<PeerConnectedEventArgs>? PeerConnected;
    public event EventHandler<PeerDisconnectedEventArgs>? PeerDisconnected;
    public event EventHandler<PeerMessageEventArgs>? MessageReceived;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Connect all synthetic peers (starts their choke timer loops)
        var tasks = new Task[_peers.Length];
        for (int i = 0; i < _peers.Length; i++)
            tasks[i] = _peers[i].ConnectAsync(InfoHash, cancellationToken);
        return Task.WhenAll(tasks);
    }

    public Task StopAsync()
    {
        var tasks = new Task[_peers.Length];
        for (int i = 0; i < _peers.Length; i++)
            tasks[i] = _peers[i].DisconnectAsync();
        return Task.WhenAll(tasks);
    }

    // ------------------------------------------------------------------
    // Query methods
    // ------------------------------------------------------------------

    public IEnumerable<IPeerConnection> GetPeersWithPiece(int pieceIndex)
    {
        int byteIndex = pieceIndex / 8;
        byte mask = (byte)(0x80 >> (pieceIndex % 8));

        foreach (var peer in _peers)
        {
            var bf = peer.PeerBitfield;
            if (byteIndex < bf.Length && (bf[byteIndex] & mask) != 0)
                yield return peer;
        }
    }

    public IEnumerable<IPeerConnection> GetAvailablePeers()
    {
        foreach (var peer in _peers)
        {
            if (!peer.IsChoked && peer.IsConnected)
                yield return peer;
        }
    }

    public IEnumerable<IPeerConnection> GetInterestedPeers()
    {
        foreach (var peer in _peers)
        {
            if (peer.IsInterested)
                yield return peer;
        }
    }

    // ------------------------------------------------------------------
    // No-op mutation methods
    // ------------------------------------------------------------------

    public Task<bool> AddPeerAsync(PeerInfo peerInfo, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task AddPeersAsync(IEnumerable<PeerInfo> peers, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RemovePeerAsync(IPeerConnection peer)
        => Task.CompletedTask;

    public IPeerConnection GetPeer(PeerInfo peerInfo)
        => _peers.FirstOrDefault(p => p.PeerInfo.Equals(peerInfo))!;

    public bool IsConnected(PeerInfo peerInfo)
        => _peers.Any(p => p.PeerInfo.Equals(peerInfo));

    public Task BroadcastHaveAsync(int pieceIndex, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task BroadcastBitfieldAsync(byte[] bitfield, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public void SetLocalBitfieldProvider(Func<byte[]?> provider) { }
    public void SetHashExchangeHandler(IHashExchangeHandler? handler) { }
    public void SetPeerExtensionSetup(Action<IPeerConnection>? callback) { }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private void OnPeerMessageReceived(object? sender, PeerMessageReceivedEventArgs e)
    {
        if (sender is IPeerConnection peer)
            MessageReceived?.Invoke(this, new PeerMessageEventArgs(peer, e.Message));
    }

    // ------------------------------------------------------------------
    // IDisposable
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var peer in _peers)
        {
            peer.MessageReceived -= OnPeerMessageReceived;
            peer.Dispose();
        }
    }
}
