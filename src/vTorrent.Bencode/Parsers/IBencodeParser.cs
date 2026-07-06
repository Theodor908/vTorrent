using vTorrent.Bencode.Objects;
using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Parsers
{
    public interface IBencodeParser
    {

        Encoding Encoding { get; }
        IBObject Parse(ReadOnlySpan<byte> data, out int bytesConsumed);

    }
}
