using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Objects
{
    public interface IBObject
    {
        int GetSizeInBytes();

        // Span-based encoding
        int EncodeTo(Span<byte> destination);

        // Stream-based encoding
        TStream EncodeTo<TStream>(TStream stream) where TStream : Stream;
        ValueTask EncodeToAsync(Stream stream, CancellationToken cancellationToken = default);

        // PipeWriter encoding
        void EncodeTo(PipeWriter pipeWriter);
        ValueTask EncodeToAsync(PipeWriter pipeWriter, CancellationToken cancellationToken = default);

    }
}
