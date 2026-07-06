using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Interfaces.Transport;

namespace vTorrent.Core.Network.Proxy;

/// <summary>
/// Connects through a proxy server. Takes hostname+port (not IPEndPoint)
/// to support DNS resolution by the proxy (preventing DNS leaks).
/// </summary>
public interface IProxyConnector
{
    Task<ITransportStream> ConnectThroughProxyAsync(
        string hostname, int port, CancellationToken ct = default);
}
