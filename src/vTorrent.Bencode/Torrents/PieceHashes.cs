using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Torrents
{
    public sealed class PieceHashes
    {
        private readonly byte[] _hashes;

        public int Count { get; }
        public const int HashSize = 20;  // SHA-1

        public PieceHashes(int pieceCount)
        {
            if (pieceCount <= 0)
                throw new ArgumentException("Piece count must be positive");

            Count = pieceCount;
            _hashes = new byte[pieceCount * HashSize];
        }

        public PieceHashes(byte[] hashes)
        {
            if (hashes == null)
                throw new ArgumentNullException(nameof(hashes));
            if (hashes.Length % HashSize != 0)
                throw new ArgumentException($"Hash data must be multiple of {HashSize} bytes");

            _hashes = hashes.ToArray();  // Defensive copy
            Count = _hashes.Length / HashSize;
        }

        public ReadOnlySpan<byte> GetPieceHash(int index)
        {
            if (index < 0 || index >= Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _hashes.AsSpan(index * HashSize, HashSize);
        }

        public void SetPieceHash(int index, ReadOnlySpan<byte> hash)
        {
            if (index < 0 || index >= Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (hash.Length != HashSize)
                throw new ArgumentException($"Hash must be exactly {HashSize} bytes");

            hash.CopyTo(_hashes.AsSpan(index * HashSize));
        }

        public byte[] ToByteArray() => _hashes.ToArray();

        public string GetPieceHashHex(int index)
        {
            return Convert.ToHexString(GetPieceHash(index));
        }
    }
}
