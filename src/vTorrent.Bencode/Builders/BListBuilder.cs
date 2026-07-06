using vTorrent.Bencode.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Builders
{
    public sealed class BListBuilder
    {
        private readonly BList _list;

        public BListBuilder()
        {
            _list = new BList();
        }

        public BListBuilder(int capacity)
        {
            _list = new BList(capacity);
        }

        public BListBuilder Add(IBObject item)
        {
            _list.Add(item);
            return this;
        }

        public BListBuilder AddString(string value)
        {
            _list.Add(new BString(value));
            return this;
        }

        public BListBuilder AddNumber(long value)
        {
            _list.Add(new BNumber(value));
            return this;
        }

        public BListBuilder AddList(Action<BListBuilder> configure)
        {
            var builder = new BListBuilder();
            configure(builder);
            _list.Add(builder.Build());
            return this;
        }

        public BListBuilder AddDictionary(Action<BDictionaryBuilder> configure)
        {
            var builder = new BDictionaryBuilder();
            configure(builder);
            _list.Add(builder.Build());
            return this;
        }

        public BListBuilder AddRange(IEnumerable<IBObject> items)
        {
            _list.AddRange(items);
            return this;
        }

        public BList Build() => _list;

        public static implicit operator BList(BListBuilder builder) => builder.Build();
    }
}
