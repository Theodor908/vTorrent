using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Tokens;
using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Bencode.Tokens;

namespace vTorrent.Bencode.Objects
{
    public sealed class BString : IBObject, IComparable<BString>, IEquatable<BString>
    {
        private readonly byte[] _value;
        private readonly Encoding _encoding;

        public ReadOnlyMemory<byte> Value => _value;
        public Encoding Encoding => _encoding;

        public BString(ReadOnlySpan<byte> value, Encoding encoding = null)
        {
            _value = value.ToArray();
            _encoding = encoding ?? Encoding.UTF8;
        }

        public BString(byte[] value, Encoding encoding = null)
        {
            _value = value?.ToArray() ?? throw new ArgumentNullException(nameof(value));
            _encoding = encoding ?? Encoding.UTF8;
        }

        public BString(string value, Encoding encoding = null)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            _encoding = encoding ?? Encoding.UTF8;
            _value = _encoding.GetBytes(value);
        }

        public override string ToString() => ToString(_encoding);

        public string ToString(Encoding encoding)
        {
            if (encoding == null) throw new ArgumentNullException(nameof(encoding));
            return encoding.GetString(_value);
        }

        public int GetSizeInBytes()
        {
            // Format: "<length>:<data>"
            var lengthDigits = _value.Length == 0 ? 1 : (int)Math.Floor(Math.Log10(_value.Length)) + 1;
            return lengthDigits + 1 + _value.Length; // digits + ':' + data
        }

        public int EncodeTo(Span<byte> destination)
        {
            var size = GetSizeInBytes();
            if (destination.Length < size)
                throw new ArgumentException($"Destination too small. Need {size} bytes, have {destination.Length}");

            var position = 0;

            var lengthStr = _value.Length.ToString();
            var lengthBytes = Encoding.ASCII.GetBytes(lengthStr);
            lengthBytes.CopyTo(destination.Slice(position));
            position += lengthBytes.Length;

            destination[position++] = BencodeTokens.StringDelimiter;

            _value.CopyTo(destination.Slice(position));
            position += _value.Length;

            return position;
        }

        public TStream EncodeTo<TStream>(TStream stream) where TStream : Stream
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            // Write length
            var lengthStr = _value.Length.ToString();
            var lengthBytes = Encoding.ASCII.GetBytes(lengthStr);
            stream.Write(lengthBytes);

            // Write colon
            stream.WriteByte(BencodeTokens.StringDelimiter);

            // Write data
            stream.Write(_value);

            return stream;
        }

        public async ValueTask EncodeToAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            // Write length
            var lengthStr = _value.Length.ToString();
            var lengthBytes = Encoding.ASCII.GetBytes(lengthStr);
            await stream.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);

            // Write colon
            await stream.WriteAsync(new byte[] { BencodeTokens.StringDelimiter }, cancellationToken).ConfigureAwait(false);

            // Write data
            await stream.WriteAsync(_value, cancellationToken).ConfigureAwait(false);
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

            EncodeTo(pipeWriter);
            await pipeWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public bool Equals(BString other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return _value.AsSpan().SequenceEqual(other._value);
        }

        public int CompareTo(BString other)
        {
            if (other is null) return 1;
            return _value.AsSpan().SequenceCompareTo(other._value);
        }

        public override bool Equals(object obj) => obj is BString other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.AddBytes(_value);
            return hash.ToHashCode();
        }

        public static bool operator ==(BString left, BString right)
        {
            if (left is null) return right is null;
            return left.Equals(right);
        }

        public static bool operator !=(BString left, BString right) => !(left == right);

        public static bool operator <(BString left, BString right)
        {
            if (left is null) return right is not null;
            return left.CompareTo(right) < 0;
        }

        public static bool operator >(BString left, BString right)
        {
            if (left is null) return false;
            return left.CompareTo(right) > 0;
        }

        public static bool operator <=(BString left, BString right) => !(left > right);
        public static bool operator >=(BString left, BString right) => !(left < right);

        public static implicit operator string(BString bstring) => bstring?.ToString();
        public static implicit operator BString(string str) => str == null ? null : new BString(str);
        public static implicit operator byte[](BString bstring) => bstring?._value;
        public static implicit operator ReadOnlyMemory<byte>(BString bstring) => bstring?.Value ?? default;
    }
}
