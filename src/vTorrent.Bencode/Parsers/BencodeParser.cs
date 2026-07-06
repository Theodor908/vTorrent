using vTorrent.Bencode.IO;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Tokens;
using vTorrent.Bencode.Exceptions;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Parsers
{
    public class BencodeParser : IBencodeParser
    {
        private readonly Encoding _encoding;

        public Encoding Encoding => _encoding;

        public BencodeParser(Encoding encoding = null)
        {
            _encoding = encoding ?? Encoding.UTF8;
        }

        public IBObject Parse(ReadOnlySpan<byte> data, out int bytesConsumed)
        {
            if(data.IsEmpty)
            {
                throw new InvalidBencodeException("Cannot parse empty data");
            }

            var reader = new SpanBencodeReader(data);
            var result = ParseValue(ref reader);
            bytesConsumed = reader.Position;
            return result;
        }

        private IBObject ParseValue(ref SpanBencodeReader reader)
        {
            var peek = reader.Peek();

            return peek switch
            {
                BencodeTokens.IntegerStart => ParseNumber(ref reader),
                BencodeTokens.ListStart => ParseList(ref reader),
                BencodeTokens.DictionaryStart => ParseDictionary(ref reader),
                >= (byte)'0' and <= (byte)'9' => ParseString(ref reader),
                _ => throw new InvalidBencodeException(
                $"Invalid bencode character '{(char)peek}' at position {reader.Position}")
            };
        }
        
        //...//

        private BNumber ParseNumber(ref SpanBencodeReader reader)
        {
            reader.Expect(BencodeTokens.IntegerStart);

            var negative = false;
            if(reader.Peek() == (byte)'-')
            {
                negative = true;
                reader.Read();
            }

            long value = 0;
            while(reader.Peek() != BencodeTokens.EndOfType)
            {
                var digit = reader.Read();
                if (digit < (byte)'0' || digit > (byte)'9')
                    throw new InvalidBencodeException($"Invalid digit in integer at position {reader.Position}");

                value = value * 10 + (digit - (byte)'0');
            }

            reader.Expect(BencodeTokens.EndOfType);
            return new BNumber(negative ? -value : value);
        }

        private BString ParseString(ref SpanBencodeReader reader)
        {
            int length = 0;
            while (reader.Peek() != BencodeTokens.StringDelimiter)
            {
                var digit = reader.Read();
                if (digit < (byte)'0' || digit > (byte)'9')
                    throw new InvalidBencodeException($"Invalid digit in string length at position {reader.Position}");

                length = length * 10 + (digit - (byte)'0');
            }

            reader.Expect((byte)':');

            var data = reader.ReadBytes(length);
            return new BString(data, _encoding);
        }

        private BList ParseList(ref SpanBencodeReader reader)
        {
            reader.Expect(BencodeTokens.ListStart);

            var list = new BList();
            while (reader.Peek() != (byte)'e')
            {
                list.Add(ParseValue(ref reader)); // Recursive!
            }

            reader.Expect(BencodeTokens.EndOfType);
            return list;
        }

        private BDictionary ParseDictionary(ref SpanBencodeReader reader)
        {
            reader.Expect(BencodeTokens.DictionaryStart);

            var dict = new BDictionary();
            while (reader.Peek() != BencodeTokens.EndOfType)
            { 
                if (reader.Peek() < (byte)'0' || reader.Peek() > (byte)'9')
                    throw new InvalidBencodeException($"Dictionary key must be a string at position {reader.Position}");

                var key = ParseString(ref reader);
                var value = ParseValue(ref reader);
                dict[key] = value;
            }

            reader.Expect(BencodeTokens.EndOfType);
            return dict;
        }

    }
}
