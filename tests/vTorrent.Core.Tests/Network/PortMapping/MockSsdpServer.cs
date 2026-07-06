using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Core.Tests.Network.PortMapping;

/// <summary>
/// Mock SSDP server for UPnP client testing.
/// Listens for M-SEARCH requests on UDP and responds with configurable SSDP responses.
/// </summary>
public sealed class MockSsdpServer : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private int _requestCount;

    public string LocationUrl { get; set; } = "";
    public string SearchTarget { get; set; } = "upnp:rootdevice";
    public bool Silent { get; set; } = false;
    public int Port { get; }
    public int RequestCount => _requestCount;

    public MockSsdpServer()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    }

    public void Start()
    {
        _listenTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var result = await _udp.ReceiveAsync(_cts.Token);
                    Interlocked.Increment(ref _requestCount);

                    if (Silent) continue;

                    var request = Encoding.ASCII.GetString(result.Buffer);
                    if (!request.Contains("M-SEARCH")) continue;

                    var response = $"HTTP/1.1 200 OK\r\n" +
                        $"ST: {SearchTarget}\r\n" +
                        $"Location: {LocationUrl}\r\n" +
                        $"USN: uuid:test-device::upnp:rootdevice\r\n" +
                        $"Cache-Control: max-age=1800\r\n" +
                        $"Server: MockUPnP/1.0\r\n" +
                        $"\r\n";

                    var bytes = Encoding.ASCII.GetBytes(response);
                    await _udp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_listenTask != null)
            try { await _listenTask; } catch { }
        _udp.Dispose();
        _cts.Dispose();
    }
}
