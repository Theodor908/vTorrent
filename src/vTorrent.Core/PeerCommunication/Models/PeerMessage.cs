using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.PeerCommunication.Models
{
    /// <summary>
    /// Represents a BitTorrent peer protocol message.
    /// Uses ArrayPool for block data to reduce GC pressure during high-speed transfers.
    /// </summary>
    public class PeerMessage
    {
        public MessageType Type { get; }
        public byte[] Payload { get; }
        public int Length => 1 + Payload.Length;

        public PeerMessage(MessageType type, byte[] payload = null)
        {
            Type = type;
            Payload = payload ?? Array.Empty<byte>();
        }

        /// <summary>
        /// Gets the total size of the serialized message including length prefix.
        /// </summary>
        public int TotalSize => 4 + Length;

        public byte[] ToBytes()
        {
            byte[] message = new byte[4 + Length]; // 4 bytes for length prefix + message

            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(0, 4), Length);

            message[4] = (byte)Type;

            if (Payload.Length > 0)
            {
                Buffer.BlockCopy(Payload, 0, message, 5, Payload.Length);
            }

            return message;
        }

        /// <summary>
        /// Writes the message directly to a buffer at the specified offset.
        /// Returns the number of bytes written.
        /// Use this for batching to avoid intermediate allocations.
        /// </summary>
        public int WriteTo(byte[] buffer, int offset)
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset, 4), Length);
            buffer[offset + 4] = (byte)Type;

            if (Payload.Length > 0)
            {
                Buffer.BlockCopy(Payload, 0, buffer, offset + 5, Payload.Length);
            }

            return TotalSize;
        }

        public static PeerMessage FromBytes(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                throw new ArgumentException("Message data cannot be null or empty");
            }

            return FromBytes(data, data.Length);
        }

        /// <summary>
        /// Parses a PeerMessage from a buffer with explicit length.
        /// Use this when the buffer may be larger than the actual message (e.g., ArrayPool buffers).
        /// </summary>
        public static PeerMessage FromBytes(byte[] data, int length)
        {
            return FromBytes(data, 0, length);
        }

        /// <summary>
        /// Parses a PeerMessage from a buffer at a given offset with explicit length.
        /// Use this when parsing from a read-ahead buffer where messages are not at position 0.
        /// </summary>
        public static PeerMessage FromBytes(byte[] data, int offset, int length)
        {
            if (data == null || length == 0)
            {
                throw new ArgumentException("Message data cannot be null or empty");
            }

            MessageType type = (MessageType)data[offset];
            byte[] payload = new byte[length - 1];

            if (payload.Length > 0)
            {
                Buffer.BlockCopy(data, offset + 1, payload, 0, payload.Length);
            }

            return new PeerMessage(type, payload);
        }


        #region Factory Methods
        public static byte[] CreateKeepAlive()
        {
            return new byte[4];
        }

        public static PeerMessage CreateChoke()
        {
            return new PeerMessage(MessageType.Choke);
        }

        public static PeerMessage CreateUnchoke()
        {
            return new PeerMessage (MessageType.Unchoke);
        }

        public static PeerMessage CreateInterested()
        {
            return new PeerMessage(MessageType.Interested);
        }

        public static PeerMessage CreateNotInterested()
        {
            return new PeerMessage(MessageType.NotInterested);
        }

        public static PeerMessage CreateHave(int pieceIndex)
        {
            byte[] payload = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(payload, pieceIndex);
            return new PeerMessage(MessageType.Have, payload);
        }

        public static PeerMessage CreateBitfield(byte[] bitfield)
        {
            if (bitfield == null || bitfield.Length == 0)
                throw new ArgumentException("Bitfield cannot be null or empty");

            return new PeerMessage(MessageType.Bitfield, bitfield);
        }
        public static PeerMessage CreateRequest(int pieceIndex, int begin, int length)
        {
            byte[] payload = new byte[12];
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), pieceIndex);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4, 4), begin);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8, 4), length);
            return new PeerMessage(MessageType.Request, payload);
        }

        public static PeerMessage CreatePiece(int pieceIndex, int begin, byte[] block)
        {
            if (block == null || block.Length == 0)
                throw new ArgumentException("Block data cannot be null or empty");

            byte[] payload = new byte[8 + block.Length];
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), pieceIndex);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4, 4), begin);
            Buffer.BlockCopy(block, 0, payload, 8, block.Length);

            return new PeerMessage(MessageType.Piece, payload);
        }

        public static PeerMessage CreateCancel(int pieceIndex, int begin, int length)
        {
            byte[] payload = new byte[12];
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), pieceIndex);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4, 4), begin);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8, 4), length);
            return new PeerMessage(MessageType.Cancel, payload);
        }

        public static PeerMessage CreatePort(ushort port)
        {
            byte[] payload = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(payload, port);
            return new PeerMessage(MessageType.Port, payload);
        }
        
        public static PeerMessage CreateHaveAll()
        {
            return new PeerMessage(MessageType.HaveAll);
        }

        public static PeerMessage CreateHaveNone()
        {
            return new PeerMessage(MessageType.HaveNone);
        }

        public static PeerMessage CreateSuggestPiece(int pieceIndex)
        {
            byte[] payload = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(payload, pieceIndex);
            return new PeerMessage(MessageType.SuggestPiece, payload);
        }

        public static PeerMessage CreateRejectRequest(int pieceIndex, int begin, int length)
        {
            byte[] payload = new byte[12];
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), pieceIndex);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4, 4), begin);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8, 4), length);
            return new PeerMessage(MessageType.RejectRequest, payload);
        }

        public static PeerMessage CreateAllowedFast(int pieceIndex)
        {
            byte[] payload = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(payload, pieceIndex);
            return new PeerMessage(MessageType.AllowedFast, payload);
        }

        /// <summary>
        /// Creates an Extended message (BEP 10).
        /// </summary>
        /// <param name="extensionId">The extension message ID (0 = handshake).</param>
        /// <param name="data">The extension message payload.</param>
        public static PeerMessage CreateExtended(byte extensionId, byte[] data)
        {
            if (data == null)
                data = Array.Empty<byte>();

            byte[] payload = new byte[1 + data.Length];
            payload[0] = extensionId;
            if (data.Length > 0)
                Buffer.BlockCopy(data, 0, payload, 1, data.Length);

            return new PeerMessage(MessageType.Extended, payload);
        }

        /// <summary>
        /// Creates an extension handshake message (BEP 10).
        /// Extension ID 0 is reserved for handshake.
        /// </summary>
        public static PeerMessage CreateExtendedHandshake(byte[] handshakeData)
        {
            return CreateExtended(0, handshakeData);
        }

        #endregion

        #region Payload Parsers

        public int ParseHave()
        {
            if (Type != MessageType.Have || Payload.Length != 4)
                throw new InvalidOperationException("Invalid Have message");

            return BinaryPrimitives.ReadInt32BigEndian(Payload);
        }

        public (int pieceIndex, int begin, int length) ParseRequest()
        {
            if (Type != MessageType.Request || Payload.Length != 12)
                throw new InvalidOperationException("Invalid Request message");

            int pieceIndex = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(0, 4));
            int begin = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(4, 4));
            int length = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(8, 4));

            return (pieceIndex, begin, length);
        }

        public (int pieceIndex, int begin, byte[] block) ParsePiece()
        {
            if (Type != MessageType.Piece || Payload.Length < 8)
                throw new InvalidOperationException("Invalid Piece message");

            int pieceIndex = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(0, 4));
            int begin = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(4, 4));

            byte[] block = new byte[Payload.Length - 8];
            Buffer.BlockCopy(Payload, 8, block, 0, block.Length);

            return (pieceIndex, begin, block);
        }

        /// <summary>
        /// Parses a Piece message using ArrayPool to reduce allocations.
        /// IMPORTANT: The returned RentedBlock MUST be disposed after use to return the buffer to the pool.
        /// This is the preferred method for high-speed downloads.
        /// </summary>
        public (int pieceIndex, int begin, RentedBlock block) ParsePiecePooled()
        {
            if (Type != MessageType.Piece || Payload.Length < 8)
                throw new InvalidOperationException("Invalid Piece message");

            int pieceIndex = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(0, 4));
            int begin = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(4, 4));

            int blockLength = Payload.Length - 8;
            var rentedBlock = new RentedBlock(blockLength);
            Buffer.BlockCopy(Payload, 8, rentedBlock.Data, 0, blockLength);

            return (pieceIndex, begin, rentedBlock);
        }

        /// <summary>
        /// Parses a Piece message returning a Span view without copying.
        /// The span is only valid while the PeerMessage exists.
        /// Use this for immediate processing without keeping the data.
        /// </summary>
        public void ParsePieceSpan(out int pieceIndex, out int begin, out ReadOnlySpan<byte> block)
        {
            if (Type != MessageType.Piece || Payload.Length < 8)
                throw new InvalidOperationException("Invalid Piece message");

            pieceIndex = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(0, 4));
            begin = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(4, 4));
            block = Payload.AsSpan(8);
        }
        public (int pieceIndex, int begin, int length) ParseCancel()
        {
            if (Type != MessageType.Cancel || Payload.Length != 12)
                throw new InvalidOperationException("Invalid Cancel message");

            int pieceIndex = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(0, 4));
            int begin = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(4, 4));
            int length = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(8, 4));

            return (pieceIndex, begin, length);
        }

        public ushort ParsePort()
        {
            if (Type != MessageType.Port || Payload.Length != 2)
                throw new InvalidOperationException("Invalid Port message");

            return BinaryPrimitives.ReadUInt16BigEndian(Payload);
        }
        
        public int ParseSuggestPiece()
        {
            if (Type != MessageType.SuggestPiece || Payload.Length != 4)
                throw new InvalidOperationException("Invalid SuggestPiece message");
            return BinaryPrimitives.ReadInt32BigEndian(Payload);
        }

        public int ParseAllowedFast()
        {
            if (Type != MessageType.AllowedFast || Payload.Length != 4)
                throw new InvalidOperationException("Invalid AllowedFast message");
            return BinaryPrimitives.ReadInt32BigEndian(Payload);
        }

        public (int pieceIndex, int begin, int length) ParseRejectRequest()
        {
            if (Type != MessageType.RejectRequest || Payload.Length != 12)
                throw new InvalidOperationException("Invalid RejectRequest message");

            int pieceIndex = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(0, 4));
            int begin = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(4, 4));
            int length = BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(8, 4));

            return (pieceIndex, begin, length);
        }

        /// <summary>
        /// Parses an Extended message (BEP 10).
        /// Returns the extension ID and payload data.
        /// </summary>
        public (byte extensionId, byte[] data) ParseExtended()
        {
            if (Type != MessageType.Extended || Payload.Length < 1)
                throw new InvalidOperationException("Invalid Extended message");

            byte extensionId = Payload[0];
            byte[] data = new byte[Payload.Length - 1];

            if (data.Length > 0)
                Buffer.BlockCopy(Payload, 1, data, 0, data.Length);

            return (extensionId, data);
        }

        /// <summary>
        /// Checks if this is an extension handshake message (extension ID = 0).
        /// </summary>
        public bool IsExtendedHandshake()
        {
            if (Type != MessageType.Extended || Payload.Length < 1)
                return false;
            return Payload[0] == 0;
        }

        #endregion

        public override string ToString()
        {
            return $"{Type} (Length: {Length}, Payload: {Payload.Length} bytes)";
        }
    }

    /// <summary>
    /// A block of data rented from ArrayPool. MUST be disposed to return the buffer.
    /// Used to reduce GC pressure during high-speed downloads (hundreds of blocks/second).
    /// </summary>
    public sealed class RentedBlock : IDisposable
    {
        private byte[] _buffer;
        private bool _disposed;

        /// <summary>
        /// The actual data. Length is the exact block size, not the rented buffer size.
        /// </summary>
        public byte[] Data => _buffer;

        /// <summary>
        /// The actual length of valid data (may be less than Data.Length due to pooling).
        /// </summary>
        public int Length { get; }

        public RentedBlock(int length)
        {
            Length = length;
            _buffer = ArrayPool<byte>.Shared.Rent(length);
        }

        /// <summary>
        /// Gets a span of the valid data portion.
        /// </summary>
        public Span<byte> AsSpan() => _buffer.AsSpan(0, Length);

        /// <summary>
        /// Gets a memory of the valid data portion.
        /// </summary>
        public Memory<byte> AsMemory() => _buffer.AsMemory(0, Length);

        public void Dispose()
        {
            if (!_disposed && _buffer != null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
                _disposed = true;
            }
        }
    }
}