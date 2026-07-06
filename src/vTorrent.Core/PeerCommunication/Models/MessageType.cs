using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.PeerCommunication.Models
{
    /// <summary>
    /// BitTorrent peer wire protocol message types.
    /// Each message has a single-byte ID that identifies its type.
    /// </summary>
    public enum MessageType : byte
    {
        /// <summary>
        /// Choke message - tells peer we won't upload to them.
        /// Payload: none
        /// </summary>
        Choke = 0,

        /// <summary>
        /// Unchoke message - tells peer we will upload to them.
        /// Payload: none
        /// </summary>
        Unchoke = 1,

        /// <summary>
        /// Interested message - tells peer we want pieces from them.
        /// Payload: none
        /// </summary>
        Interested = 2,

        /// <summary>
        /// Not Interested message - tells peer we don't want pieces from them.
        /// Payload: none
        /// </summary>
        NotInterested = 3,

        /// <summary>
        /// Have message - announces we have completed a piece.
        /// Payload: 4-byte piece index
        /// </summary>
        Have = 4,

        /// <summary>
        /// Bitfield message - sends our complete piece availability.
        /// Usually sent right after handshake.
        /// Payload: bitfield (variable length)
        /// </summary>
        Bitfield = 5,

        /// <summary>
        /// Request message - requests a block of a piece.
        /// Payload: piece index (4 bytes), begin offset (4 bytes), length (4 bytes)
        /// </summary>
        Request = 6,

        /// <summary>
        /// Piece message - delivers a requested block.
        /// Payload: piece index (4 bytes), begin offset (4 bytes), block data (variable)
        /// </summary>
        Piece = 7,

        /// <summary>
        /// Cancel message - cancels a previous request.
        /// Payload: piece index (4 bytes), begin offset (4 bytes), length (4 bytes)
        /// </summary>
        Cancel = 8,

        /// <summary>
        /// Port message - announces DHT listening port.
        /// Payload: 2-byte port number
        /// </summary>
        Port = 9,
        
        // BEP 6 Fast Extension Messages
        
        SuggestPiece = 13,
        HaveAll = 14,
        HaveNone = 15,
        RejectRequest = 16,
        AllowedFast = 17,
        
        // BEP 10 Extension Protocol
        Extended = 20,

        // BEP 52 Hash Exchange
        HashRequest = 21,
        Hashes = 22,
        HashReject = 23,

        // Internal sentinel for keep-alive messages in the send channel (not a wire protocol ID)
        KeepAlive = 255,
    }
}
