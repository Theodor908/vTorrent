using vTorrent.Bencode.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Exceptions
{
    public class InvalidBencodeException : Exception
    {
        public int Position { get; }
        public byte? FoundByte { get; }
        public byte? ExpectedByte { get; }

        public InvalidBencodeException(string message, int position = -1, byte? found = null, byte? expected = null) : base(FormatMessage(message, position, found, expected))
        {
            Position = position;
            FoundByte = found;
            ExpectedByte = expected;
        }

        private static string FormatMessage(string message, int position, byte? found, byte? expected)
        {
            var sb = new StringBuilder(message);

            if (position >= 0)
            {
                sb.Append($" at position {position}");
            }

            if (found.HasValue && expected.HasValue)
            {
                sb.Append($" (expected '{(char)expected.Value}', found '{(char)found.Value}')");
            }
            else if (found.HasValue)
            { 
                sb.Append($" (found '{(char)found.Value}')");
            }

            return sb.ToString();
        }
    }

    public class InvalidBencodeException<T> : InvalidBencodeException where T : IBObject
    {
        public Type ExpectedType => typeof(T);
        public Type ActualType {  get; }

        public InvalidBencodeException(string message, Type actualType = null) : base(message)
        {
            ActualType = actualType;
        }
    }
}
