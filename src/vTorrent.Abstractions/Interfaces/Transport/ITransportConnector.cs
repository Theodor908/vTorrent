using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Abstractions.Interfaces.Transport;

public interface ITransportConnector
{
    Task<ITransportStream> ConnectAsync(EndPoint endpoint, CancellationToken ct = default);
}
