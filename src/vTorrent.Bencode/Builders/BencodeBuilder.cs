using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Builders
{
    public static class BencodeBuilder
    {
        public static BDictionaryBuilder Dictionary() => new BDictionaryBuilder();
        public static BListBuilder List() => new BListBuilder();
    }
}
