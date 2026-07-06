// tests/vTorrent.Core.Tests/Network/I2P/MockSamBridge.cs
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace vTorrent.Core.Tests.Network.I2P;

/// <summary>
/// Fake SAM bridge for unit testing. Listens on localhost,
/// responds to SAM protocol commands with configurable replies.
/// </summary>
public sealed class MockSamBridge : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<string> _receivedCommands = new();
    private readonly ConcurrentDictionary<string, string> _responses = new();
    private Task? _acceptTask;
    private int _disposed;

    public int Port { get; }
    public IReadOnlyCollection<string> ReceivedCommands => _receivedCommands;

    public MockSamBridge()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void SetResponse(string commandPrefix, string response)
    {
        _responses[commandPrefix] = response;
    }

    public void SetDefaultHandshake()
    {
        SetResponse("HELLO", "HELLO REPLY RESULT=OK VERSION=3.3\n");
    }

    public void SetDefaultSessionCreate(string destination = "TRANSIENT_BASE64_DEST")
    {
        SetResponse("SESSION CREATE", $"SESSION STATUS RESULT=OK DESTINATION={destination}\n");
    }

    public void SetDefaultDestGenerate(string pub = "TEST_PUB_DEST", string priv = "TEST_PRIV_KEY")
    {
        SetResponse("DEST GENERATE", $"DEST REPLY PUB={pub} PRIV={priv}\n");
    }

    public void SetDefaultStreamConnect()
    {
        SetResponse("STREAM CONNECT", "STREAM STATUS RESULT=OK\n");
    }

    public void SetDefaultNamingLookup(string name, string value)
    {
        SetResponse("NAMING LOOKUP", $"NAMING REPLY RESULT=OK NAME={name} VALUE={value}\n");
    }

    public async Task StartAsync()
    {
        _acceptTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = HandleClientAsync(client, _cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        });
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch { break; }
            if (line == null) break;

            _receivedCommands.Add(line);

            var response = _responses
                .Where(kv => line.StartsWith(kv.Key))
                .Select(kv => kv.Value)
                .FirstOrDefault();

            if (response != null)
                await writer.WriteAsync(response);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        _listener.Stop();
        if (_acceptTask != null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch { /* expected on cancellation */ }
        }
        _cts.Dispose();
    }
}
