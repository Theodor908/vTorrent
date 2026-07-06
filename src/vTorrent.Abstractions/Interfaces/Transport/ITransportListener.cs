using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Abstractions.Interfaces.Transport;

public interface ITransportListener : IAsyncDisposable
{
    Task StartAsync(EndPoint bindEndpoint, CancellationToken ct = default);
    Task<ITransportStream> AcceptAsync(CancellationToken ct = default);
}
