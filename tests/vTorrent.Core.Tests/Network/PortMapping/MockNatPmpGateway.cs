using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Core.Tests.Network.PortMapping;

/// <summary>
/// Mock NAT-PMP/PCP gateway for unit testing.
/// Listens on UDP, responds to NAT-PMP/PCP binary packets.
/// </summary>
public sealed class MockNatPmpGateway : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private int _requestCount;

    // Configurable response behavior
    public bool RespondWithPcpUnsupported { get; set; } = false;
    public ushort ResponseExternalPort { get; set; } = 12345;
    public uint ResponseLifetime { get; set; } = 3600;
    public byte[] ResponseExternalIp { get; set; } = { 203, 0, 113, 1 }; // 203.0.113.1
    public uint Epoch { get; set; } = 1000;
    public bool Silent { get; set; } = false; // Don't respond (for timeout tests)

    public int Port { get; }
    public int RequestCount => _requestCount;

    public MockNatPmpGateway()
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

                    var request = result.Buffer;
                    byte[] response;

                    if (request.Length >= 2 && request[0] == 2) // PCP v2
                    {
                        if (RespondWithPcpUnsupported)
                            response = BuildPcpErrorResponse(1); // unsupported version
                        else
                            response = BuildPcpMapResponse(request);
                    }
                    else if (request.Length >= 2 && request[0] == 0) // NAT-PMP v0
                    {
                        if (request[1] == 0) // GetPublicAddress
                            response = BuildNatPmpPublicAddressResponse();
                        else // MAP (opcode 1=UDP, 2=TCP)
                            response = BuildNatPmpMapResponse(request);
                    }
                    else
                    {
                        continue;
                    }

                    await _udp.SendAsync(response, result.RemoteEndPoint, _cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        });
    }

    private byte[] BuildNatPmpPublicAddressResponse()
    {
        var buf = new byte[12];
        buf[0] = 0; // version
        buf[1] = 128; // opcode 0 + 128
        // result = 0 (success) at bytes 2-3
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), Epoch);
        Buffer.BlockCopy(ResponseExternalIp, 0, buf, 8, 4);
        return buf;
    }

    private byte[] BuildNatPmpMapResponse(byte[] request)
    {
        var buf = new byte[16];
        buf[0] = 0; // version
        buf[1] = (byte)(request[1] + 128); // opcode + 128
        // result = 0 at bytes 2-3
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), Epoch);
        // internal port from request
        buf[8] = request[4]; buf[9] = request[5];
        // external port
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(10), ResponseExternalPort);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12), ResponseLifetime);
        return buf;
    }

    private byte[] BuildPcpMapResponse(byte[] request)
    {
        var buf = new byte[60];
        buf[0] = 2; // PCP version
        buf[1] = (byte)(request[1] | 0x80); // R bit set
        // result = 0 at byte 3
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), ResponseLifetime);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), Epoch);
        // Copy nonce from request (bytes 24-35 → response bytes 24-35)
        if (request.Length >= 36)
            Buffer.BlockCopy(request, 24, buf, 24, 12);
        buf[36] = request.Length > 36 ? request[36] : (byte)6; // protocol
        // internal port
        if (request.Length >= 42)
        {
            buf[40] = request[40]; buf[41] = request[41];
        }
        // external port
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(42), ResponseExternalPort);
        // external IP (IPv4-mapped IPv6)
        buf[54] = 0xFF; buf[55] = 0xFF;
        Buffer.BlockCopy(ResponseExternalIp, 0, buf, 56, 4);
        return buf;
    }

    private byte[] BuildPcpErrorResponse(byte resultCode)
    {
        var buf = new byte[24]; // minimal PCP response header
        buf[0] = 2;
        buf[1] = 0x81; // MAP + R bit
        buf[3] = resultCode;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), Epoch);
        return buf;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udp.Dispose();
        if (_listenTask != null)
            try { await _listenTask; } catch { }
        _cts.Dispose();
    }
}
