using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using vTorrent.Bencode.Exceptions;

namespace vTorrent.Bencode.IO
{
    public ref struct SpanBencodeReader
    {
        private ReadOnlySpan<byte> _data;
        private int _position;

        public int Position => _position;
        public int Remaining => _data.Length - _position;

        public SpanBencodeReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public byte Peek()
        {
            if(_position >=  _data.Length)
            {
                throw new InvalidBencodeException("Unexpected end of data");
            }
            return _data[_position];
        }

        public byte Read()
        {
            var b = Peek();
            _position++;
            return b;
        }

        public void Expect(byte expected)
        {
            var actual = Read();
            if (actual != expected)
            {
                throw new InvalidBencodeException($"Expected '{(char)expected}' but found '{(char)actual}' at position {_position - 1}");
            }
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if(_position + count > _data.Length)
            {
                throw new InvalidBencodeException($"Not enough data. Need {count} bytes but only {Remaining} available");
            }

            var result = _data.Slice( _position, count );
            _position += count;
            return result;
        }
    }
}
