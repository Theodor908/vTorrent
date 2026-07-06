using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Abstractions.Models;

namespace vTorrent.Core.DHT
{
    /// <summary>
    /// Interface for DHT peer discovery that can be used by TorrentEngine.
    /// </summary>
    public interface IDhtPeerSource
    {
        /// <summary>
        /// Whether DHT is currently running.
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Registers a torrent for DHT peer discovery and announcement.
        /// </summary>
        /// <param name="infoHash">The torrent's info_hash (20 bytes)</param>
        /// <param name="listenPort">The port we're listening on for peers</param>
        void RegisterTorrent(byte[] infoHash, int listenPort);

        /// <summary>
        /// Unregisters a torrent from DHT.
        /// </summary>
        /// <param name="infoHash">The torrent's info_hash (20 bytes)</param>
        void UnregisterTorrent(byte[] infoHash);

        /// <summary>
        /// Looks up peers for a torrent via DHT.
        /// </summary>
        /// <param name="infoHash">The torrent's info_hash (20 bytes)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>List of discovered peers</returns>
        Task<List<PeerInfo>> LookupPeersAsync(byte[] infoHash, CancellationToken cancellationToken = default);

        /// <summary>
        /// Announces a torrent to the DHT network.
        /// </summary>
        /// <param name="infoHash">The torrent's info_hash (20 bytes)</param>
        /// <param name="port">The port we're listening on</param>
        /// <param name="cancellationToken">Cancellation token</param>
        Task AnnounceAsync(byte[] infoHash, int port, CancellationToken cancellationToken = default);

        /// <summary>
        /// Event raised when peers are discovered for a torrent.
        /// </summary>
        event Action<byte[], List<PeerInfo>> PeersDiscovered;
    }
}
