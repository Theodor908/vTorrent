using vTorrent.Bencode.Tokens;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Objects
{
    public sealed class BList : IBObject, IList<IBObject>, IReadOnlyList<IBObject>
    {
        private readonly List<IBObject> _items;

        public BList() => _items = new List<IBObject>();
        public BList(int capacity) => _items = new List<IBObject>(capacity);
        public BList(IEnumerable<IBObject> items) => _items = new List<IBObject>(items);

        public int Count => _items.Count;
        public bool IsReadOnly => false;

        public IBObject this[int index]
        {
            get => _items[index];
            set => _items[index] = value ?? throw new ArgumentNullException(nameof(value));
        }

        public void Add(IBObject item) => _items.Add(item ?? throw new ArgumentNullException(nameof(item)));

        public void AddRange(IEnumerable<IBObject> items) => _items.AddRange(items);

        public void AddString(string value) => Add(new BString(value));

        public void AddNumber(long value) => Add(new BNumber(value));

        public void AddBytes(byte[] value) => Add(new BString(value));

        public void Insert(int index, IBObject item) => _items.Insert(index, item);
        public bool Remove(IBObject item) => _items.Remove(item);
        public void RemoveAt(int index) => _items.RemoveAt(index);
        public void Clear() => _items.Clear();
        public bool Contains(IBObject item) => _items.Contains(item);
        public void CopyTo(IBObject[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public int IndexOf(IBObject item) => _items.IndexOf(item);

        public T Get<T>(int index) where T : IBObject
        {
            var item = _items[index];

            try
            {
                return (T)item;
            }
            catch (InvalidCastException)
            {
                throw new InvalidCastException(
                    $"Item at index {index} is {item.GetType().Name}, not {typeof(T).Name}");
            }
        }

        public IEnumerable<T> GetAll<T>() where T : IBObject => _items.OfType<T>();

        public IEnumerator<IBObject> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public int GetSizeInBytes()
        {
            var size = 2; // 'l' and 'e'
            foreach (var item in _items)
                size += item.GetSizeInBytes();
            return size;
        }

        public int EncodeTo(Span<byte> destination)
        {
            var size = GetSizeInBytes();
            if (destination.Length < size)
                throw new ArgumentException($"Destination too small. Need {size} bytes");

            var position = 0;
            destination[position++] = BencodeTokens.ListStart;

            foreach (var item in _items)
            {
                position += item.EncodeTo(destination.Slice(position));
            }

            destination[position++] = BencodeTokens.EndOfType;
            return position;
        }

        public TStream EncodeTo<TStream>(TStream stream) where TStream : Stream
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            stream.WriteByte(BencodeTokens.ListStart);

            foreach (var item in _items)
            {
                item.EncodeTo(stream);
            }

            stream.WriteByte(BencodeTokens.EndOfType);

            return stream;
        }

        public async ValueTask EncodeToAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            await stream.WriteAsync(new byte[] { BencodeTokens.ListStart }, cancellationToken).ConfigureAwait(false);

            foreach (var item in _items)
            {
                await item.EncodeToAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            await stream.WriteAsync(new byte[] { BencodeTokens.EndOfType }, cancellationToken).ConfigureAwait(false);
        }

        public void EncodeTo(PipeWriter pipeWriter)
        {
            if (pipeWriter == null) throw new ArgumentNullException(nameof(pipeWriter));

            var span = pipeWriter.GetSpan(1);
            span[0] = BencodeTokens.ListStart;
            pipeWriter.Advance(1);

            foreach (var item in _items)
            {
                item.EncodeTo(pipeWriter);
            }

            span = pipeWriter.GetSpan(1);
            span[0] = BencodeTokens.EndOfType;
            pipeWriter.Advance(1);
        }

        public async ValueTask EncodeToAsync(PipeWriter pipeWriter, CancellationToken cancellationToken = default)
        {
            if (pipeWriter == null) throw new ArgumentNullException(nameof(pipeWriter));

            EncodeTo(pipeWriter);
            await pipeWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override string ToString() => $"BList[{Count}]";
    }
}
