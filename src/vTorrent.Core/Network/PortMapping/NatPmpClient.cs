using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Core.Network.PortMapping;

/// <summary>
/// NAT-PMP (RFC 6886) / PCP (RFC 6887) client.
/// Tries PCP v2 first, falls back to NAT-PMP v0 on unsupported version error.
/// </summary>
public sealed class NatPmpClient : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly IPAddress _gatewayIp;
    private readonly int _gatewayPort;
    private readonly int _maxRetries;
    private readonly int _baseRetryMs;
    private readonly List<PortMapping> _activeMappings = new();

    private int _protocolVersion = 2; // Start with PCP
    private uint _lastEpoch;
    private int _nextMappingId;

    public NatPmpClient(IPAddress gatewayIp, int gatewayPort = 5351,
        int maxRetries = 9, int baseRetryMs = 250)
    {
        _gatewayIp = gatewayIp;
        _gatewayPort = gatewayPort;
        _maxRetries = maxRetries;
        _baseRetryMs = baseRetryMs;
        _udp = new UdpClient();
        _udp.Connect(gatewayIp, gatewayPort);
    }

    /// <summary>Request a port mapping.</summary>
    public async Task<PortMapping?> AddMappingAsync(PortMapProtocol protocol,
        int internalPort, int externalPort, uint lifetime = 3600,
        CancellationToken ct = default)
    {
        byte[] request;
        if (_protocolVersion == 2)
        {
            request = BuildPcpMapRequest((byte)protocol, (ushort)internalPort,
                (ushort)externalPort, lifetime);
        }
        else
        {
            byte opcode = protocol == PortMapProtocol.Udp ? (byte)1 : (byte)2;
            request = BuildNatPmpMapRequest(opcode, (ushort)internalPort,
                (ushort)externalPort, lifetime);
        }

        var response = await SendWithRetryAsync(request, ct);
        if (response == null) return null;

        // Check for PCP version error → fallback
        if (_protocolVersion == 2 && response.Length >= 4 && response[3] == 1)
        {
            _protocolVersion = 0; // Fall back to NAT-PMP
            return await AddMappingAsync(protocol, internalPort, externalPort, lifetime, ct);
        }

        // Parse response
        var mapping = ParseMappingResponse(response, protocol, internalPort);
        if (mapping != null)
            _activeMappings.Add(mapping);

        return mapping;
    }

    /// <summary>Delete a port mapping (sends lifetime=0).</summary>
    public async Task<bool> DeleteMappingAsync(PortMapping mapping, CancellationToken ct = default)
    {
        var result = await AddMappingAsync(mapping.Protocol,
            mapping.InternalPort, 0, lifetime: 0, ct);

        if (result != null)
        {
            _activeMappings.RemoveAll(m => m.Id == mapping.Id);
            return true;
        }
        return false;
    }

    /// <summary>Get the external (public) IP address from the gateway.</summary>
    public async Task<IPAddress?> GetExternalAddressAsync(CancellationToken ct = default)
    {
        if (_protocolVersion == 2)
        {
            // PCP returns external IP in MAP response — use a dummy mapping
            // For simplicity, fall back to NAT-PMP GetPublicAddress
            _protocolVersion = 0;
        }

        var request = new byte[] { 0, 0 }; // NAT-PMP GetPublicAddress
        var response = await SendWithRetryAsync(request, ct);

        if (response == null || response.Length < 12) return null;

        var resultCode = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2));
        if (resultCode != 0) return null;

        var epoch = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(4));
        CheckEpoch(epoch);

        var ipBytes = new byte[4];
        Buffer.BlockCopy(response, 8, ipBytes, 0, 4);
        return new IPAddress(ipBytes);
    }

    public IReadOnlyList<PortMapping> ActiveMappings => _activeMappings;

    private async Task<byte[]?> SendWithRetryAsync(byte[] request, CancellationToken ct)
    {
        for (int retry = 0; retry < _maxRetries; retry++)
        {
            await _udp.SendAsync(request, ct);
            var timeout = TimeSpan.FromMilliseconds(_baseRetryMs * (1 << retry));
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delayCts.CancelAfter(timeout);
            try
            {
                var result = await _udp.ReceiveAsync(delayCts.Token);
                return result.Buffer;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue;
            }
        }
        return null;
    }

    private PortMapping? ParseMappingResponse(byte[] response, PortMapProtocol protocol, int internalPort)
    {
        if (_protocolVersion == 0 && response.Length >= 16)
        {
            var resultCode = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2));
            if (resultCode != 0) return null;

            var epoch = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(4));
            CheckEpoch(epoch);

            var extPort = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(10));
            var lifetime = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(12));

            return new PortMapping
            {
                Id = Interlocked.Increment(ref _nextMappingId),
                Protocol = protocol,
                Transport = PortMapTransport.NatPmp,
                InternalPort = internalPort,
                ExternalPort = extPort,
                Expiry = DateTime.UtcNow.AddSeconds(lifetime)
            };
        }
        else if (_protocolVersion == 2 && response.Length >= 60)
        {
            var resultCode = response[3];
            if (resultCode != 0) return null;

            var lifetime = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(4));
            var epoch = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(8));
            CheckEpoch(epoch);

            var extPort = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(42));

            // Extract external IP from IPv4-mapped IPv6 (bytes 56-59)
            IPAddress? extIp = null;
            if (response[54] == 0xFF && response[55] == 0xFF)
            {
                var ipBytes = new byte[4];
                Buffer.BlockCopy(response, 56, ipBytes, 0, 4);
                extIp = new IPAddress(ipBytes);
            }

            return new PortMapping
            {
                Id = Interlocked.Increment(ref _nextMappingId),
                Protocol = protocol,
                Transport = PortMapTransport.NatPmp,
                InternalPort = internalPort,
                ExternalPort = extPort,
                ExternalAddress = extIp,
                Expiry = DateTime.UtcNow.AddSeconds(lifetime)
            };
        }

        return null;
    }

    private void CheckEpoch(uint epoch)
    {
        if (epoch < _lastEpoch)
        {
            // Gateway rebooted — would need to re-create all mappings
            // The PortMappingManager handles this via periodic refresh
        }
        _lastEpoch = epoch;
    }

    private static byte[] BuildNatPmpMapRequest(byte opcode, ushort internalPort,
        ushort externalPort, uint lifetime)
    {
        var buf = new byte[12];
        buf[0] = 0; // NAT-PMP version
        buf[1] = opcode;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), internalPort);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6), externalPort);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), lifetime);
        return buf;
    }

    private static byte[] BuildPcpMapRequest(byte protocol, ushort internalPort,
        ushort externalPort, uint lifetime)
    {
        var buf = new byte[60];
        buf[0] = 2; // PCP version
        buf[1] = 1; // MAP opcode
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), lifetime);
        // Client IP as IPv4-mapped IPv6 at bytes 8-23
        // ::ffff:127.0.0.1
        buf[18] = 0xFF; buf[19] = 0xFF;
        buf[20] = 127; buf[23] = 1;
        // Random nonce at bytes 24-35
        RandomNumberGenerator.Fill(buf.AsSpan(24, 12));
        buf[36] = protocol;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(40), internalPort);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(42), externalPort);
        return buf;
    }

    public async ValueTask DisposeAsync()
    {
        _udp.Dispose();
    }
}
