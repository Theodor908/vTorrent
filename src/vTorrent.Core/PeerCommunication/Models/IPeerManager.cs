using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using vTorrent.Core.PeerCommunication.Events;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.PeerCommunication.Models
{
    public interface IPeerManager : IDisposable
    {
        int ConnectedPeerCount { get; }
        int MaxConnections { get; }
        IReadOnlyList<IPeerConnection> ConnectedPeers { get; }
        byte[] InfoHash { get; }
        long TotalBytesDownloaded { get; }
        long TotalBytesUploaded { get; }

        void SetLocalBitfieldProvider(Func<byte[]?> provider);

        /// <summary>BEP 52: Set handler for hash exchange messages on all new connections.</summary>
        void SetHashExchangeHandler(IHashExchangeHandler? handler);

        /// <summary>BEP 54: Set a callback invoked on each new PeerConnection before the handshake, for extension registration.</summary>
        void SetPeerExtensionSetup(Action<IPeerConnection>? callback);

        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync();

        Task<bool> AddPeerAsync(PeerInfo peerInfo, CancellationToken cancellationToken = default);
        Task AddPeersAsync(IEnumerable<PeerInfo> peers, CancellationToken cancellationToken = default);
        Task RemovePeerAsync(IPeerConnection peer);
        IPeerConnection GetPeer(PeerInfo peerInfo);

        bool IsConnected(PeerInfo peerInfo);
        
        /// <summary>
        /// When true, BroadcastHaveAsync is suppressed — pieces are only revealed
        /// through the super-seed mechanism.
        /// </summary>
        bool SuperSeedingActive { get; set; }

        Task BroadcastHaveAsync(int pieceIndex, CancellationToken cancellationToken = default);
        Task BroadcastBitfieldAsync(byte[] bitfield, CancellationToken cancellationToken = default);

        IEnumerable<IPeerConnection> GetPeersWithPiece(int pieceIndex);
        IEnumerable<IPeerConnection> GetAvailablePeers();
        IEnumerable<IPeerConnection> GetInterestedPeers();


        event EventHandler<PeerConnectedEventArgs> PeerConnected;

        event EventHandler<PeerDisconnectedEventArgs> PeerDisconnected;

        event EventHandler<PeerMessageEventArgs> MessageReceived;
    }
}
