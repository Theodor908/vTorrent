using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Tasks;
using vTorrent.Abstractions.Interfaces.Transport;

namespace vTorrent.Core.TrackerCommunication.Udp;

/// <summary>
/// Routes incoming UDP tracker responses to pending requests by transaction ID.
/// Mirrors libtorrent tracker_manager::incoming_packet().
/// Registered as the tracker handler on UdpSocketManager.
/// </summary>
public sealed class UdpTrackerPacketHandler : IUdpPacketHandler
{
    private readonly ConcurrentDictionary<int, PendingTrackerRequest> _pending = new();

    /// <summary>
    /// Registers a pending tracker request. The TCS will be completed when a matching
    /// response arrives from the expected endpoint.
    /// </summary>
    public void RegisterPending(int transactionId, TaskCompletionSource<byte[]> tcs, IPEndPoint expectedSender)
    {
        _pending[transactionId] = new PendingTrackerRequest(tcs, expectedSender);
    }

    /// <summary>
    /// Removes a pending request (called in finally block after timeout or completion).
    /// </summary>
    public void UnregisterPending(int transactionId)
    {
        _pending.TryRemove(transactionId, out _);
    }

    /// <summary>
    /// IUdpPacketHandler implementation. Called by UdpSocketManager for incoming tracker packets.
    /// Extracts transaction ID from bytes 4-7, looks up pending request, validates sender,
    /// and completes the TCS with an owned copy of the data.
    /// </summary>
    public void ProcessPacket(ReadOnlyMemory<byte> data, IPEndPoint sender)
    {
        if (data.Length < 8) return;

        int transactionId = BinaryPrimitives.ReadInt32BigEndian(data.Span.Slice(4, 4));

        if (!_pending.TryGetValue(transactionId, out var pending)) return;

        // Validate sender address matches expected tracker (libtorrent parity)
        if (!pending.ExpectedSender.Address.Equals(sender.Address)) return;

        // Copy data to owned array — UdpSocketManager's buffer is pooled and reused
        var ownedCopy = data.ToArray();

        pending.Tcs.TrySetResult(ownedCopy);
    }

    private readonly record struct PendingTrackerRequest(
        TaskCompletionSource<byte[]> Tcs,
        IPEndPoint ExpectedSender);
}
