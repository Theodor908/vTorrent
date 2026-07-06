using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.PeerCommunication.Utilities
{
    public class Bitfield
    {
        private readonly object _lock = new();
        private readonly byte[] _data;
        private readonly int _pieceCount;
        private int _completedPieces;

        public byte[] Data => _data;
        public int PieceCount => _pieceCount;
        public int CompletePieces { get { lock (_lock) return _completedPieces; } }
        public double Progress { get { lock (_lock) return _pieceCount > 0 ? (double)_completedPieces / _pieceCount : 0.0; } }
        public bool IsComplete { get { lock (_lock) return _completedPieces == _pieceCount; } }

        public Bitfield(int pieceCount)
        {
            if(pieceCount <= 0)
            {
                throw new ArgumentException("Piece count must be positive", nameof(pieceCount));
            }

            _pieceCount = pieceCount;
            _data = new byte[(pieceCount + 7) / 8];
            _completedPieces = 0;
        }

        public Bitfield(byte[] data, int pieceCount)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (pieceCount <= 0)
                throw new ArgumentException("Piece count must be positive", nameof(pieceCount));

            int expectedLength = (pieceCount + 7) / 8;
            if (data.Length != expectedLength)
                throw new ArgumentException($"Data length {data.Length} doesn't match expected length {expectedLength}");

            _data = (byte[])data.Clone();
            _pieceCount = pieceCount;
            _completedPieces = CountSetBitsUnsafe();
        }

        public bool HasPiece(int pieceIndex)
        {
            ValidatePieceIndex(pieceIndex);

            int byteIndex = pieceIndex / 8;
            int bitIndex = 7 - (pieceIndex % 8);  // MSB-first: piece 0 = bit 7

            lock (_lock)
                return (_data[byteIndex] & (1 << bitIndex)) != 0;
        }

        public void SetPiece(int pieceIndex, bool value = true)
        {
            ValidatePieceIndex(pieceIndex);

            int byteIndex = pieceIndex / 8;
            int bitIndex = 7 - (pieceIndex % 8);  // MSB-first: piece 0 = bit 7

            lock (_lock)
            {
                bool currentValue = (_data[byteIndex] & (1 << bitIndex)) != 0;

                if (value && !currentValue)
                {
                    _data[byteIndex] |= (byte)(1 << bitIndex);
                    _completedPieces++;
                }
                else if (!value && currentValue)
                {
                    _data[byteIndex] &= (byte)~(1 << bitIndex);
                    _completedPieces--;
                }
            }
        }

        public void ClearPiece(int pieceIndex)
        {
            SetPiece(pieceIndex, false);
        }

        public void SetAll()
        {
            lock (_lock)
            {
                for (int i = 0; i < _data.Length; i++)
                {
                    _data[i] = 0xFF;
                }

                int extraBits = _data.Length * 8 - _pieceCount;
                if (extraBits > 0)
                {
                    byte mask = (byte)(0xFF << extraBits);
                    _data[_data.Length - 1] &= mask;
                }

                _completedPieces = _pieceCount;
            }
        }

        public void ClearAll()
        {
            lock (_lock)
            {
                Array.Clear(_data, 0, _data.Length);
                _completedPieces = 0;
            }
        }

        public int[] GetAvailablePieces()
        {
            lock (_lock)
            {
                var pieces = new int[_completedPieces];
                int index = 0;

                for (int i = 0; i < _pieceCount; i++)
                {
                    if (HasPieceUnsafe(i))
                    {
                        pieces[index++] = i;
                    }
                }

                return pieces;
            }
        }

        public int[] GetMissingPieces()
        {
            lock (_lock)
            {
                var pieces = new int[_pieceCount - _completedPieces];
                int index = 0;

                for (int i = 0; i < _pieceCount; i++)
                {
                    if (!HasPieceUnsafe(i))
                    {
                        pieces[index++] = i;
                    }
                }

                return pieces;
            }
        }

        public void Or(Bitfield other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }
            if (other._pieceCount != _pieceCount)
            {
                throw new ArgumentException("Bitfields must have same piece count");
            }

            lock (_lock)
            {
                for (int i = 0; i < _data.Length; i++)
                {
                    _data[i] |= other._data[i];
                }

                _completedPieces = CountSetBitsUnsafe();
            }
        }

        public void And(Bitfield other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }
            if (other._pieceCount != _pieceCount)
            {
                throw new ArgumentException("Bitfields must have same piece count");
            }

            lock (_lock)
            {
                for (int i = 0; i < _data.Length; i++)
                {
                    _data[i] &= other._data[i];
                }

                _completedPieces = CountSetBitsUnsafe();
            }
        }

        public Bitfield Clone()
        {
            lock (_lock)
                return new Bitfield((byte[])_data.Clone(), _pieceCount);
        }

        public int CountDifferences(Bitfield other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other));
            if (other._pieceCount != _pieceCount)
                throw new ArgumentException("Bitfields must have same piece count");

            lock (_lock)
            {
                int differences = 0;

                for (int i = 0; i < _data.Length; i++)
                {
                    byte xor = (byte)(_data[i] ^ other._data[i]);
                    differences += BitOperations.PopCount((uint)xor);
                }

                return differences;
            }
        }

        /// <summary>Lock-free HasPiece for use inside already-locked methods.</summary>
        private bool HasPieceUnsafe(int pieceIndex)
        {
            int byteIndex = pieceIndex / 8;
            int bitIndex = 7 - (pieceIndex % 8);
            return (_data[byteIndex] & (1 << bitIndex)) != 0;
        }

        /// <summary>Lock-free CountSetBits using hardware POPCNT. For use inside already-locked methods.</summary>
        private int CountSetBitsUnsafe()
        {
            int count = 0;

            // Process 8 bytes at a time using ulong PopCount (single-cycle POPCNT on x86 SSE4.2+)
            var ulongSpan = MemoryMarshal.Cast<byte, ulong>(_data.AsSpan(0, _data.Length & ~7));
            foreach (var word in ulongSpan)
                count += BitOperations.PopCount(word);

            // Handle remaining bytes (0-7 bytes)
            for (int i = ulongSpan.Length * 8; i < _data.Length; i++)
                count += BitOperations.PopCount((uint)_data[i]);

            // Mask off trailing bits beyond _pieceCount (MSB-first: piece 0 = bit 7 of byte 0)
            int totalBits = _data.Length * 8;
            int excessBits = totalBits - _pieceCount;
            if (excessBits > 0)
            {
                // The excess bits are the LOW bits of the last byte
                byte lastByte = _data[_data.Length - 1];
                byte excessMask = (byte)((1 << excessBits) - 1);
                count -= BitOperations.PopCount((uint)(lastByte & excessMask));
            }

            return count;
        }

        private void ValidatePieceIndex(int pieceIndex)
        {
            if (pieceIndex < 0 || pieceIndex >= _pieceCount)
                throw new ArgumentOutOfRangeException(nameof(pieceIndex), $"Piece index must be between 0 and {_pieceCount - 1}");
        }

        public override string ToString()
        {
            return $"Bitfield [{_completedPieces}/{_pieceCount} pieces ({Progress:P1})]";
        }

        public string ToBinaryString()
        {
            if (_pieceCount > 64)
                return $"[Too large to display: {_pieceCount} pieces]";

            var bits = new char[_pieceCount];
            for (int i = 0; i < _pieceCount; i++)
            {
                bits[i] = HasPiece(i) ? '1' : '0';
            }

            return new string(bits);
        }

    }
}
