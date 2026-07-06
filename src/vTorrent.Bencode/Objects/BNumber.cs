using vTorrent.Bencode.Tokens;
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Objects
{
    public sealed class BNumber : IBObject, IComparable<BNumber>, IEquatable<BNumber>
    {
        private readonly long _value;

        public long Value => _value;

        public BNumber(long value) => _value = value;

        public int GetSizeInBytes()
        {
            // Format: "i<number>e"
            var digits = _value == 0 ? 1 : (int)Math.Floor(Math.Log10(Math.Abs(_value))) + 1;
            var negativeSign = _value < 0 ? 1 : 0;
            return 1 + negativeSign + digits + 1; 
        }

        public int EncodeTo(Span<byte> destination)
        {
            var size = GetSizeInBytes();
            if (destination.Length < size)
                throw new ArgumentException($"Destination too small. Need {size} bytes, have {destination.Length}");

            var position = 0;
            destination[position++] = BencodeTokens.IntegerStart;

            var valueStr = _value.ToString();
            var valueBytes = Encoding.ASCII.GetBytes(valueStr);
            valueBytes.CopyTo(destination.Slice(position));
            position += valueBytes.Length;

            destination[position++] = BencodeTokens.EndOfType;

            return size;
        }


        public TStream EncodeTo<TStream>(TStream stream) where TStream : Stream
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            // For small objects like numbers, it's fine to allocate a small buffer
            Span<byte> buffer = stackalloc byte[32]; // Max size for long is ~21 bytes
            var bytesWritten = EncodeTo(buffer);
            stream.Write(buffer.Slice(0, bytesWritten));

            return stream;
        }

        public async ValueTask EncodeToAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            var size = GetSizeInBytes();
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                var bytesWritten = EncodeTo(buffer.AsSpan());
                await stream.WriteAsync(buffer.AsMemory(0, bytesWritten), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void EncodeTo(PipeWriter pipeWriter)
        {
            if (pipeWriter == null) throw new ArgumentNullException(nameof(pipeWriter));

            var size = GetSizeInBytes();
            var buffer = pipeWriter.GetSpan(size);
            var bytesWritten = EncodeTo(buffer);
            pipeWriter.Advance(bytesWritten);
        }

        public async ValueTask EncodeToAsync(PipeWriter pipeWriter, CancellationToken cancellationToken = default)
        {
            if (pipeWriter == null) throw new ArgumentNullException(nameof(pipeWriter));

            EncodeTo(pipeWriter); // Write to buffer
            await pipeWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public bool Equals(BNumber other) => other != null && _value == other._value;
        public int CompareTo(BNumber other) => _value.CompareTo(other?._value ?? 0);

        public override bool Equals(object obj) => obj is BNumber other && Equals(other);
        public override int GetHashCode() => _value.GetHashCode();
        public override string ToString() => _value.ToString();

        public static implicit operator long(BNumber bn) => bn._value;
        public static implicit operator BNumber(long value) => new BNumber(value);
        public static implicit operator int(BNumber bn) => (int)bn._value;
        public static implicit operator BNumber(int value) => new BNumber(value);
    }
}
