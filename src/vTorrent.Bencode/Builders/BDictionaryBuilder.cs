using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using vTorrent.Bencode.Objects;

namespace vTorrent.Bencode.Builders
{
    public sealed class BDictionaryBuilder
    {
        private readonly BDictionary _dict;

        public BDictionaryBuilder()
        {
            _dict = new BDictionary();
        }

        public BDictionaryBuilder Add(string key, IBObject value)
        {
            _dict.Add(key, value);
            return this;
        }

        public BDictionaryBuilder AddString(string key, string value)
        {
            _dict.Add(key, new BString(value));
            return this;
        }

        public BDictionaryBuilder AddNumber(string key, long value)
        {
            _dict.Add(key, new BNumber(value));
            return this;
        }

        public BDictionaryBuilder AddBytes(string key, byte[] value)
        {
            _dict.Add(key, new BString(value));
            return this;
        }

        public BDictionaryBuilder AddList(string key, Action<BListBuilder> configure)
        {
            var builder = new BListBuilder();
            configure(builder);
            _dict.Add(key, builder.Build());
            return this;
        }

        public BDictionaryBuilder AddDictionary(string key, Action<BDictionaryBuilder> configure)
        {
            var builder = new BDictionaryBuilder();
            configure(builder);
            _dict.Add(key, builder.Build());
            return this;
        }

        public BDictionaryBuilder AddIfNotNull(string key, IBObject? value)
        {
            if (value != null)
                _dict.Add(key, value);
            return this;
        }

        public BDictionaryBuilder AddIfNotNullOrEmpty(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                _dict.Add(key, new BString(value));
            return this;
        }

        public BDictionary Build() => _dict;

        public static implicit operator BDictionary(BDictionaryBuilder builder) => builder.Build();
    }
}