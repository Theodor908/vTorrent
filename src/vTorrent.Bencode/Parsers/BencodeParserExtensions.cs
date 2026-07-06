using vTorrent.Bencode.Objects;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Parsers
{
    public static class BencodeParserExtensions
    {
        public static IBObject Parse(this IBencodeParser parser, ReadOnlySpan<byte> data) 
            => parser.Parse(data, out _);
        public static IBObject Parse(this IBencodeParser parser, byte[] data)
            => parser.Parse(data.AsSpan(), out _);

        public static IBObject Parse(this IBencodeParser parser, ReadOnlyMemory<byte> data)
            => parser.Parse(data.Span, out _);

        public static T Parse<T>(this IBencodeParser parser, ReadOnlySpan<byte> data)
        where T : class, IBObject
        {
            var result = parser.Parse(data, out _);
            return result as T ?? throw new InvalidCastException(
                $"Expected {typeof(T).Name}, but got {result.GetType().Name}");
        }

        public static IBObject Parse(this IBencodeParser parser, Stream stream)
        {
            if (parser == null) throw new ArgumentNullException(nameof(parser));
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            // For seekable streams, we can get exact length
            if (stream.CanSeek)
            {
                var length = (int)(stream.Length - stream.Position);
                var buffer = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    var totalRead = 0;
                    while (totalRead < length)
                    {
                        var read = stream.Read(buffer, totalRead, length - totalRead);
                        if (read == 0)
                            throw new EndOfStreamException("Unexpected end of stream");
                        totalRead += read;
                    }

                    return parser.Parse(buffer.AsSpan(0, totalRead), out _);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            else
            {
                // For non-seekable streams, read in chunks
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return parser.Parse(ms.ToArray());
            }
        }

        public static T Parse<T>(this IBencodeParser parser, Stream stream)
            where T : class, IBObject
        {
            var result = parser.Parse(stream);
            return result as T ?? throw new InvalidCastException(
                $"Expected {typeof(T).Name}, but got {result.GetType().Name}");
        }

        public static async ValueTask<IBObject> ParseAsync(
            this IBencodeParser parser,
            Stream stream,
            CancellationToken cancellationToken = default)
        {
            if (parser == null) throw new ArgumentNullException(nameof(parser));
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            // For seekable streams, we can get exact length
            if (stream.CanSeek)
            {
                var length = (int)(stream.Length - stream.Position);
                var buffer = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    var totalRead = 0;
                    while (totalRead < length)
                    {
                        var read = await stream.ReadAsync(
                            buffer.AsMemory(totalRead, length - totalRead),
                            cancellationToken).ConfigureAwait(false);

                        if (read == 0)
                            throw new EndOfStreamException("Unexpected end of stream");

                        totalRead += read;
                    }

                    return parser.Parse(buffer.AsSpan(0, totalRead), out _);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            else
            {
                // For non-seekable streams, read in chunks
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                return parser.Parse(ms.ToArray());
            }
        }

        public static async ValueTask<T> ParseAsync<T>(
            this IBencodeParser parser,
            Stream stream,
            CancellationToken cancellationToken = default)
            where T : class, IBObject
        {
            var result = await parser.ParseAsync(stream, cancellationToken).ConfigureAwait(false);
            return result as T ?? throw new InvalidCastException(
                $"Expected {typeof(T).Name}, but got {result.GetType().Name}");
        }

        public static async ValueTask<IBObject> ParseAsync(
            this IBencodeParser parser,
            PipeReader pipeReader,
            CancellationToken cancellationToken = default)
        {
            if (parser == null) throw new ArgumentNullException(nameof(parser));
            if (pipeReader == null) throw new ArgumentNullException(nameof(pipeReader));

            // Read all data from pipe
            var result = await pipeReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            try
            {
                // Check if we have all the data
                if (buffer.IsSingleSegment)
                {
                    // Fast path - single segment
                    return parser.Parse(buffer.FirstSpan, out _);
                }
                else
                {
                    // Slow path - multiple segments, need to copy to contiguous array
                    var data = ArrayPool<byte>.Shared.Rent((int)buffer.Length);
                    try
                    {
                        buffer.CopyTo(data);
                        return parser.Parse(data.AsSpan(0, (int)buffer.Length), out _);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(data);
                    }
                }
            }
            finally
            {
                pipeReader.AdvanceTo(buffer.End);
            }
        }

        public static async ValueTask<T> ParseAsync<T>(
            this IBencodeParser parser,
            PipeReader pipeReader,
            CancellationToken cancellationToken = default)
            where T : class, IBObject
        {
            var result = await parser.ParseAsync(pipeReader, cancellationToken).ConfigureAwait(false);
            return result as T ?? throw new InvalidCastException(
                $"Expected {typeof(T).Name}, but got {result.GetType().Name}");
        }

        public static IBObject ParseFile(this IBencodeParser parser, string filePath)
        {
            if (parser == null) throw new ArgumentNullException(nameof(parser));
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            var bytes = File.ReadAllBytes(filePath);
            return parser.Parse(bytes);
        }

        public static T ParseFile<T>(this IBencodeParser parser, string filePath)
            where T : class, IBObject
        {
            var result = parser.ParseFile(filePath);
            return result as T ?? throw new InvalidCastException(
                $"Expected {typeof(T).Name}, but got {result.GetType().Name}");
        }

        public static async ValueTask<IBObject> ParseFileAsync(
            this IBencodeParser parser,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            if (parser == null) throw new ArgumentNullException(nameof(parser));
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            return parser.Parse(bytes);
        }

        public static async ValueTask<T> ParseFileAsync<T>(
            this IBencodeParser parser,
            string filePath,
            CancellationToken cancellationToken = default)
            where T : class, IBObject
        {
            var result = await parser.ParseFileAsync(filePath, cancellationToken).ConfigureAwait(false);
            return result as T ?? throw new InvalidCastException(
                $"Expected {typeof(T).Name}, but got {result.GetType().Name}");
        }
    }

}