using System.Threading;

namespace vTorrent.Core.Network.IpFilter;

public sealed class IpFilterHolder
{
    private volatile IpFilter _current = new();

    public IpFilter Current => _current;

    public void Update(IpFilter newFilter)
    {
        Interlocked.Exchange(ref _current, newFilter);
    }
}
