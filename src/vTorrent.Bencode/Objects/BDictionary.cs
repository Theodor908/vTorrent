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
    public sealed class BDictionary : IBObject, IDictionary<BString, IBObject>, IReadOnlyDictionary<BString, IBObject>
    {
        private readonly SortedDictionary<BString, IBObject> _dict;

        public BDictionary() => _dict = new SortedDictionary<BString, IBObject>();
        public BDictionary(IComparer<BString> comparer) => _dict = new SortedDictionary<BString, IBObject>(comparer);

        public int Count => _dict.Count;
        public bool IsReadOnly => false;

        public IBObject this[BString key]
        {
            get => _dict[key];
            set => _dict[key] = value ?? throw new ArgumentNullException(nameof(value));
        }

        public IBObject this[string key]
        {
            get => this[new BString(key)];
            set => this[new BString(key)] = value;
        }

        public void Add(BString key, IBObject value) => _dict.Add(key, value);
        public void Add(KeyValuePair<BString, IBObject> item) => Add(item.Key, item.Value);

        public void Add(string key, IBObject value) => Add(new BString(key), value);
        public void AddString(string key, string value) => Add(key, new BString(value));
        public void AddNumber(string key, long value) => Add(key, new BNumber(value));
        public void AddBytes(string key, byte[] value) => Add(key, new BString(value));

        public bool TryGetValue(BString key, out IBObject value) => _dict.TryGetValue(key, out value);
        public bool TryGetValue(string key, out IBObject value) => _dict.TryGetValue(new BString(key), out value);

        public bool ContainsKey(BString key) => _dict.ContainsKey(key);
        public bool ContainsKey(string key) => _dict.ContainsKey(new BString(key));
        public bool Contains(KeyValuePair<BString, IBObject> item) => _dict.Contains(item);

        public bool Remove(BString key) => _dict.Remove(key);
        public bool Remove(string key) => _dict.Remove(new BString(key));
        public bool Remove(KeyValuePair<BString, IBObject> item) => _dict.Remove(item.Key);

        public void Clear() => _dict.Clear();
        public void CopyTo(KeyValuePair<BString, IBObject>[] array, int arrayIndex)
            => ((ICollection<KeyValuePair<BString, IBObject>>)_dict).CopyTo(array, arrayIndex);

        public T Get<T>(string key) where T : class, IBObject
        {
            if (!TryGetValue(key, out var value))
                throw new KeyNotFoundException($"Key '{key}' not found");

            return value as T ?? throw new InvalidCastException(
                $"Value for key '{key}' is {value.GetType().Name}, not {typeof(T).Name}");
        }

        public T GetOrDefault<T>(string key, T defaultValue = null) where T : class, IBObject
        {
            return TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;
        }

        public string GetString(string key) => Get<BString>(key);
        public string GetStringOrDefault(string key, string defaultValue = null)
            => GetOrDefault<BString>(key)?.ToString() ?? defaultValue;

        public long GetNumber(string key) => Get<BNumber>(key);
        public long GetNumberOrDefault(string key, long defaultValue = 0)
            => GetOrDefault<BNumber>(key)?.Value ?? defaultValue;

        public BList GetList(string key) => Get<BList>(key);
        public BList GetListOrDefault(string key) => GetOrDefault<BList>(key);

        public BDictionary GetDictionary(string key) => Get<BDictionary>(key);
        public BDictionary GetDictionaryOrDefault(string key) => GetOrDefault<BDictionary>(key);

        public void MergeWith(BDictionary other, bool overwrite = true)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));

            foreach (var (key, value) in other)
            {
                if (overwrite || !ContainsKey(key))
                    this[key] = value;
            }
        }

        public ICollection<BString> Keys => _dict.Keys;
        public ICollection<IBObject> Values => _dict.Values;
        IEnumerable<BString> IReadOnlyDictionary<BString, IBObject>.Keys => _dict.Keys;
        IEnumerable<IBObject> IReadOnlyDictionary<BString, IBObject>.Values => _dict.Values;

        public IEnumerator<KeyValuePair<BString, IBObject>> GetEnumerator() => _dict.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public int GetSizeInBytes()
        {
            var size = 2; // 'd' and 'e'
            foreach (var (key, value) in _dict)
            {
                size += key.GetSizeInBytes();
                size += value.GetSizeInBytes();
            }
            return size;
        }

        public int EncodeTo(Span<byte> destination)
        {
            var size = GetSizeInBytes();
            if (destination.Length < size)
                throw new ArgumentException($"Destination too small. Need {size} bytes");

            var position = 0;
            destination[position++] = BencodeTokens.DictionaryStart;

            // Keys already sorted in SortedDictionary
            foreach (var (key, value) in _dict)
            {
                position += key.EncodeTo(destination.Slice(position));
                position += value.EncodeTo(destination.Slice(position));
            }

            destination[position++] = BencodeTokens.EndOfType;
            return position;
        }

        public TStream EncodeTo<TStream>(TStream stream) where TStream : Stream
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            stream.WriteByte(BencodeTokens.DictionaryStart);

            foreach (var (key, value) in _dict)
            {
                key.EncodeTo(stream);
                value.EncodeTo(stream);
            }

            stream.WriteByte(BencodeTokens.EndOfType);

            return stream;
        }

        public async ValueTask EncodeToAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            await stream.WriteAsync(new byte[] { BencodeTokens.DictionaryStart }, cancellationToken).ConfigureAwait(false);

            foreach (var (key, value) in _dict)
            {
                await key.EncodeToAsync(stream, cancellationToken).ConfigureAwait(false);
                await value.EncodeToAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            await stream.WriteAsync(new byte[] { BencodeTokens.EndOfType }, cancellationToken).ConfigureAwait(false);
        }

        public void EncodeTo(PipeWriter pipeWriter)
        {
            if (pipeWriter == null) throw new ArgumentNullException(nameof(pipeWriter));

            var span = pipeWriter.GetSpan(1);
            span[0] = BencodeTokens.DictionaryStart;
            pipeWriter.Advance(1);

            foreach (var (key, value) in _dict)
            {
                key.EncodeTo(pipeWriter);
                value.EncodeTo(pipeWriter);
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

        public override string ToString() => $"BDictionary[{Count}]";
    }
}
