using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Tokens
{
    public static class BencodeTokens
    {
        public const byte IntegerStart = (byte)'i';
        public const byte StringDelimiter = (byte)':';
        public const byte DictionaryStart = (byte)'d';
        public const byte ListStart = (byte)'l';
        public const byte EndOfType = (byte)'e';

    }
}
