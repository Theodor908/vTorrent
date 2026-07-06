using vTorrent.Core.PeerCommunication.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.PeerCommunication.Models
{
    public interface IPeerConnection : IDisposable
    {
        PeerInfo PeerInfo { get; }

        /// <summary>
        /// Cached string representation of the remote endpoint. Allocated once at
        /// connection time to avoid repeated IPEndPoint.ToString() allocations.
        /// </summary>
        string EndpointString { get; }

        byte[] PeerId { get; }

        bool IsChoked { get; }

        bool IsInterested { get; }

        bool IsChoking { get; }
        
        bool PeerIsInterested { get; }

        bool IsConnected { get; }

        byte[] PeerBitfield { get; set; }

        long BytesDownloaded { get; }

        long BytesUploaded { get; }

        DateTime ConnectedAt { get; }

        /// <summary>
        /// Round-trip time in milliseconds, measured from request to response.
        /// Used for calculating optimal request pipeline depth.
        /// </summary>
        double RoundTripTimeMs { get; }

        /// <summary>
        /// Indicates if this peer is snubbed (hasn't sent data despite being unchoked).
        /// </summary>
        bool IsSnubbed { get; set; }

        /// <summary>
        /// Whether this peer has all pieces (is a complete seed).
        /// Set once when bitfield is verified complete; avoids repeated O(n) scans.
        /// </summary>
        bool IsSeed { get; }

        /// <summary>
        /// Human-readable client name from BEP 10 extension handshake (e.g. "qBittorrent 4.6.2").
        /// Null if peer didn't send extension handshake.
        /// </summary>
        string? ClientName { get; }

        /// <summary>
        /// Whether this connection uses protocol encryption (RC4 stream or handshake-only).
        /// </summary>
        bool IsEncrypted { get; }

        /// <summary>
        /// Whether this is an incoming connection (peer connected to us vs. we connected to them).
        /// </summary>
        bool IsIncoming { get; }

        /// <summary>Whether this connection uses uTP transport</summary>
        bool IsUtp { get; }

        /// <summary>
        /// Whether the remote peer supports BEP 6 Fast Extensions.
        /// </summary>
        bool PeerSupportsFastExtension { get; }

        /// <summary>
        /// Remote peer's advertised request queue size from BEP 10 extension handshake ("reqq").
        /// Null if peer didn't advertise. Used to cap pipeline depth.
        /// </summary>
        int? RemoteRequestQueueSize { get; }

        Task ConnectAsync(byte[] infoHash, CancellationToken cancellationToken = default, byte[]? preReadHandshake = null);

        Task SendMessageAsync(PeerMessage message, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends multiple messages in a single network write (like libtorrent's cork/uncork pattern).
        /// </summary>
        Task SendMessagesAsync(IReadOnlyList<PeerMessage> messages, CancellationToken cancellationToken = default);

        Task<PeerMessage> ReceiveMessageAsync(CancellationToken cancellationToken = default);

        Task SendBitfieldAsync(byte[] bitfield, CancellationToken cancellationToken = default);

        Task SetInterestedAsync(bool interested, CancellationToken cancellationToken = default);

        Task SetChokingAsync(bool choking, CancellationToken cancellationToken = default);

        Task RequestBlockAsync(int pieceIndex, int begin, int length, CancellationToken cancellationToken = default);

        /// <summary>
        /// Requests multiple blocks in a single network write (like libtorrent's request batching).
        /// </summary>
        Task RequestBlocksBatchAsync(IReadOnlyList<(int pieceIndex, int begin, int length)> blocks, CancellationToken cancellationToken = default);

        Task SendBlockAsync(int pieceIndex, int begin, byte[] block, CancellationToken cancellationToken = default);

        Task CancelBlockAsync(int pieceIndex, int begin, int length, CancellationToken cancellationToken = default);

        Task AnnounceHaveAsync(int pieceIndex, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends BEP 6 HAVE_NONE message. If peer doesn't support Fast Extensions,
        /// sends an all-zero bitfield of the given length instead.
        /// </summary>
        Task SendHaveNoneAsync(int totalPieces, CancellationToken cancellationToken = default);

        Task DisconnectAsync();

        // BEP 52 Hash Exchange
        Task SendHashRequestAsync(HashRequestMessage msg, CancellationToken cancellationToken = default);
        Task SendHashesAsync(HashesMessage msg, CancellationToken cancellationToken = default);
        Task SendHashRejectAsync(HashRejectMessage msg, CancellationToken cancellationToken = default);

        event EventHandler<PeerStateChangedEventArgs> StateChanged;
        event EventHandler<PeerMessageReceivedEventArgs> MessageReceived;
        event EventHandler<PeerConnectionLostEventArgs> ConnectionLost;
    }
}
