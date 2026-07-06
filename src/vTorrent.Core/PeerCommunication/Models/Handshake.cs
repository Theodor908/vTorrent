using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.PeerCommunication.Models
{
    public class Handshake
    {
        public const int HandshakeLength = 68; // in bytes
        private const string ProtocolString = "BitTorrent protocol";
        private const byte ProtocolStringLength = 19;
        public byte[] Reserved { get; }
        public byte[] InfoHash { get; }
        public byte[] PeerId { get; }

        public Handshake(byte[] infoHash, byte[] peerId, byte[] reserved = null)
        {
            if (infoHash == null || infoHash.Length != 20)
                throw new ArgumentException("InfoHash must be exactly 20 bytes");

            if (peerId == null || peerId.Length != 20)
                throw new ArgumentException("PeerId must be exactly 20 bytes");

            InfoHash = infoHash;
            PeerId = peerId;
            Reserved = reserved ?? new byte[8];

            if (Reserved.Length != 8)
                throw new ArgumentException("Reserved must be exactly 8 bytes");
        }

        public byte[] ToBytes()
        {
            byte[] handshake = new byte[HandshakeLength];
            int offset = 0;

            // Protocol string length 1 byte
            handshake[offset++] = ProtocolStringLength;

            // Protocol string 19 bytes
            byte[] protocolBytes = Encoding.ASCII.GetBytes(ProtocolString);
            Buffer.BlockCopy(protocolBytes, 0, handshake, offset, ProtocolStringLength);
            offset += ProtocolStringLength;

            // Reserved 8 bytes
            Buffer.BlockCopy(Reserved, 0, handshake, offset, 8);
            offset += 8;

            // Info hash 20 bytes
            Buffer.BlockCopy(InfoHash, 0, handshake, offset, 20);
            offset += 20;

            // Peer ID 20 bytes
            Buffer.BlockCopy(PeerId, 00, handshake, offset, 20);

            return handshake;
        }

        public static Handshake FromBytes(byte[] data)
        {
            if (data == null || data.Length != HandshakeLength)
                throw new ArgumentException($"Handshake must be exactly {HandshakeLength} bytes");

            int offset = 0;

            // Validate protocol string length
            byte pstrLen = data[offset++];
            if (pstrLen != ProtocolStringLength)
                throw new InvalidOperationException($"Invalid protocol string length: {pstrLen}");

            // Validate protocol string
            string protocol = Encoding.ASCII.GetString(data, offset, pstrLen);
            offset += pstrLen;

            if (protocol != ProtocolString)
                throw new InvalidOperationException($"Invalid protocol string: {protocol}");

            // Extract reserved bytes
            byte[] reserved = new byte[8];
            Buffer.BlockCopy(data, offset, reserved, 0, 8);
            offset += 8;

            // Extract info hash
            byte[] infoHash = new byte[20];
            Buffer.BlockCopy(data, offset, infoHash, 0, 20);
            offset += 20;

            // Extract peer ID
            byte[] peerId = new byte[20];
            Buffer.BlockCopy(data, offset, peerId, 0, 20);

            return new Handshake(infoHash, peerId, reserved);
        }

        public bool SupportsDHT()
        {
            return (Reserved[7] & 0x01) != 0;
        }

        public bool SupportsFastExtension()
        {
            return (Reserved[7] & 0x04) != 0;
        }

        public bool SupportsExtensionProtocol()
        {
            return (Reserved[5] & 0x10) != 0;
        }

        public void SetDHTSupport(bool supported)
        {
            if(supported)
            {
                Reserved[7] |= 0x01;
            }
            else
            {
                Reserved[7] &= 0xFE;
            }
        }

        public void SetFastExtensionSupport(bool supported)
        {
            if (supported)
            {
                Reserved[7] |= 0x04;
            }
            else
            {
                Reserved[7] &= 0xFB;
            }
        }

        public void SetExtensionProtocolSupport(bool supported)
        {
            if(supported)
            {
                Reserved[5] |= 0x10;
            }
            else
            {
                Reserved[5] &= 0xEF;
            }
        }

        public static Handshake CreateWithExtensions(byte[] infoHash, byte[] peerId, bool supportDHT = true, bool supportFastExtension = true, bool supportExtensionProtocol = true)
        {
            var handshake = new Handshake(infoHash, peerId);

            if (supportDHT)
                handshake.SetDHTSupport(true);

            if (supportFastExtension)
                handshake.SetFastExtensionSupport(true);

            if (supportExtensionProtocol)
                handshake.SetExtensionProtocolSupport(true);

            return handshake;
        }

        public override string ToString()
        {
            return $"Handshake [InfoHash: {BitConverter.ToString(InfoHash).Replace("-", "")}, " +
                   $"PeerId: {Encoding.ASCII.GetString(PeerId)}, " +
                   $"DHT: {SupportsDHT()}, Fast: {SupportsFastExtension()}, Ext: {SupportsExtensionProtocol()}]";
        }
    }
}
