using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace vTorrent.Core.PeerCommunication.Transport.Utp;

/// <summary>
/// BEP 29 uTP socket — full connection state machine with LEDBAT congestion control.
/// Handles SYN/SYN-ACK, data segmentation, reassembly, retransmission, and teardown.
///
/// Thread-safety: <see cref="ProcessIncomingPacket"/> (UDP receive loop),
/// <see cref="Tick"/> (retransmission timer), and the send path (application thread)
/// all mutate the same connection state, so every state mutation is guarded by
/// <see cref="_lock"/>. Outgoing datagrams are dispatched via the (synchronous, for the
/// direct path) send callback while the lock is held — the callback never re-enters this
/// socket, so this cannot deadlock. Only <see cref="ReadAsync"/> stays lock-free: the
/// reassembly <see cref="Channel{T}"/> is single-reader and already thread-safe.
/// </summary>
public sealed class UtpSocket : IDisposable
{
    public const int MaxPayloadSize = 1400;

    // Number of retransmissions of a single data packet before the connection is declared
    // dead. Mirrors libtorrent's utp_socket_manager num_resends default (6); intentionally
    // more forgiving than vTorrent's UtpNumResends=3 so transient loss bursts recover.
    private const int MaxDataResends = 6;

    // Connection identity
    public ushort SendConnectionId { get; private set; }
    public ushort RecvConnectionId { get; private set; }
    public IPEndPoint RemoteEndPoint { get; }
    public UtpConnectionState State { get; private set; }

    // Sequence numbers
    public ushort LocalSequenceNumber => _seqNr;
    public ushort RemoteSequenceNumber => _ackNr;

    // Flow-control observation surface (tests). Racy simple reads by design.
    internal uint PeerAdvertisedWindow => _peerWindowSize;
    internal int BytesInFlight => _curWindow;

    // Internal state
    private ushort _seqNr;
    private ushort _ackNr;
    private uint _peerWindowSize;
    private uint _lastTimestampDifference;

    // Buffers
    private readonly PacketBuffer _sendBuffer = new(2048);
    private readonly PacketBuffer _recvBuffer = new(2048);
    private readonly Channel<byte[]> _reassemblyChannel;
    private ushort _nextExpectedSeqNr;

    // Retransmission timer (socket-level, libtorrent m_timeout model). Deadline in
    // Environment.TickCount64 ms; 0 = disarmed. Reset on every advancing ACK and on send;
    // when it fires we retransmit the oldest unacked packet. _numTimeouts drives RTO backoff.
    private long _rtoDeadlineMs;
    private int _numTimeouts;

    // Teardown (ST_FIN). Per BEP 29 the FIN consumes a sequence number (like ST_DATA) and
    // marks the eof position: the reader reaches EOF only once every data packet up to
    // _finSeqNr has been delivered (a FIN can arrive before a still-missing data packet).
    private bool _finReceived;
    private ushort _finSeqNr;

    // Partial read state — leftover data from a previous Channel read (reader thread only)
    private byte[] _partialBuffer;
    private int _partialOffset;

    // Congestion control
    private readonly LedbatCongestionControl _congestion = new();
    private int _curWindow; // bytes currently in flight (sent, unacked) — full packet sizes

    // Flow control. Senders block in SendDataPacketAsync when the send window is full and
    // wake when an incoming packet frees space (ACK) or grows the peer's advertised window.
    // The TCS is swapped-and-completed under _lock so a waiter that captured the task before
    // the signal always observes it (no lost wakeup).
    private TaskCompletionSource _windowSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    // The receive-window value we last advertised to the peer; used to detect when our
    // window reopens (the app reader drained the channel) so we can push a window update.
    private uint _lastAdvertisedWindow = 65536;

    // Send callback
    private readonly Func<ReadOnlyMemory<byte>, IPEndPoint, ValueTask> _sendDatagram;

    // Connection completion
    private readonly TaskCompletionSource _connectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Guards all connection state (see class remarks). Reused as reusable scratch for the
    // retransmission walk to avoid per-tick allocation.
    private readonly object _lock = new();
    private readonly List<PacketEntry> _resendScratch = new();
    private readonly List<PacketEntry> _ackScratch = new();

    private bool _disposed;

    // Factory: outgoing connection (we initiate SYN)
    public static UtpSocket CreateOutgoing(IPEndPoint remote,
        Func<ReadOnlyMemory<byte>, IPEndPoint, ValueTask> sendDatagram)
    {
        var socket = new UtpSocket(remote, sendDatagram);

        // Generate random connection IDs per BEP 29
        ushort connId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        socket.RecvConnectionId = connId;
        socket.SendConnectionId = (ushort)(connId + 1);
        socket._seqNr = 1; // BEP 29: SYN seq_nr = 1
        socket.State = UtpConnectionState.None;

        return socket;
    }

    // Factory: incoming connection (we received SYN)
    public static UtpSocket CreateIncoming(UtpPacketHeader synHeader, IPEndPoint remote,
        Func<ReadOnlyMemory<byte>, IPEndPoint, ValueTask> sendDatagram)
    {
        var socket = new UtpSocket(remote, sendDatagram);

        // Per BEP 29: recv_id = syn.connection_id + 1, send_id = syn.connection_id
        socket.RecvConnectionId = (ushort)(synHeader.ConnectionId + 1);
        socket.SendConnectionId = synHeader.ConnectionId;
        socket._seqNr = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        socket._ackNr = synHeader.SequenceNumber;
        socket._nextExpectedSeqNr = (ushort)(synHeader.SequenceNumber + 1);
        socket._peerWindowSize = synHeader.WindowSize;
        socket.State = UtpConnectionState.SynRecv;

        // Send ST_STATE (SYN-ACK). No other thread references this socket yet, so no lock.
        socket.SendControl(UtpPacketType.State);

        return socket;
    }

    private UtpSocket(IPEndPoint remote,
        Func<ReadOnlyMemory<byte>, IPEndPoint, ValueTask> sendDatagram)
    {
        RemoteEndPoint = remote ?? throw new ArgumentNullException(nameof(remote));
        _sendDatagram = sendDatagram ?? throw new ArgumentNullException(nameof(sendDatagram));
        _reassemblyChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>
    /// Initiates outgoing connection by sending SYN.
    /// Completes when SYN-ACK is received (state transitions to Connected).
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (State != UtpConnectionState.None)
                throw new InvalidOperationException($"Cannot connect in state {State}");

            // Send SYN packet
            SendControl(UtpPacketType.Syn);
            _seqNr++; // SYN consumes a sequence number
            State = UtpConnectionState.SynSent;
        }

        // Wait for SYN-ACK
        using var registration = ct.Register(() => _connectTcs.TrySetCanceled(ct));
        await _connectTcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Send data over the uTP connection. Segments into MaxPayloadSize packets.
    /// Applies flow + congestion control: blocks (respecting <paramref name="ct"/>) when the
    /// send window is full, resuming as ACKs arrive.
    /// </summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (State != UtpConnectionState.Connected && State != UtpConnectionState.SynRecv)
            throw new InvalidOperationException($"Cannot send in state {State}");

        int offset = 0;
        while (offset < data.Length)
        {
            int chunkSize = Math.Min(data.Length - offset, MaxPayloadSize);
            var chunk = data.Slice(offset, chunkSize);

            await SendDataPacketAsync(chunk, ct).ConfigureAwait(false);
            offset += chunkSize;
        }
    }

    /// <summary>
    /// Window gate mirroring libtorrent <c>send_pkt()</c>/<c>resend_packet()</c>: a new
    /// packet may go out only if it fits in <c>min(cwnd, peer_advertised_window)</c> minus
    /// bytes already in flight. The escape hatch (allow when nothing is in flight) matches
    /// libtorrent's "some packets larger than the congestion window must be allowed through,
    /// but only if we don't have any outstanding bytes" and doubles as the zero-window probe.
    /// Must be called with <see cref="_lock"/> held.
    /// </summary>
    private bool CanSendNow(int packetSize)
    {
        if (_curWindow == 0) return true;
        long effective = Math.Min((long)_congestion.CongestionWindow, _peerWindowSize);
        return _curWindow + packetSize <= effective;
    }

    /// <summary>Wake any sender blocked on the flow-control window. Call with <see cref="_lock"/> held.</summary>
    private void SignalWindowSpace()
    {
        var prev = _windowSignal;
        _windowSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        prev.TrySetResult();
    }

    /// <summary>
    /// Read received data. Blocks until data is available.
    /// </summary>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_disposed)
            return 0;

        // Return leftover data from partial buffer first
        if (_partialBuffer != null)
        {
            int remaining = _partialBuffer.Length - _partialOffset;
            int toCopy = Math.Min(remaining, buffer.Length);
            _partialBuffer.AsMemory(_partialOffset, toCopy).CopyTo(buffer);
            _partialOffset += toCopy;
            if (_partialOffset >= _partialBuffer.Length)
            {
                _partialBuffer = null;
                _partialOffset = 0;
            }
            return toCopy;
        }

        try
        {
            var data = await _reassemblyChannel.Reader.ReadAsync(ct).ConfigureAwait(false);
            int toCopy2 = Math.Min(data.Length, buffer.Length);
            data.AsMemory(0, toCopy2).CopyTo(buffer);

            if (toCopy2 < data.Length)
            {
                _partialBuffer = data;
                _partialOffset = toCopy2;
            }

            return toCopy2;
        }
        catch (ChannelClosedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Process an incoming UDP packet from the remote endpoint.
    /// Called by UtpSocketManager when a packet arrives for this connection.
    /// </summary>
    public void ProcessIncomingPacket(ReadOnlyMemory<byte> packet, IPEndPoint sender)
    {
        if (_disposed) return;

        if (!UtpPacketHeader.TryParse(packet.Span, out var header))
            return;

        lock (_lock)
        {
            if (_disposed) return;

            // Update peer window and timestamp difference
            _peerWindowSize = header.WindowSize;
            _lastTimestampDifference = header.TimestampDifferenceMicroseconds;

            // Update congestion control with delay sample
            if (header.TimestampMicroseconds > 0)
            {
                uint now = UtpTimestamp.Now();
                uint delay = now - header.TimestampMicroseconds;
                _congestion.UpdateBaseDelay(delay);
            }

            switch (header.Type)
            {
                case UtpPacketType.State:
                    ProcessState(header);
                    break;
                case UtpPacketType.Data:
                    ProcessData(header, packet.Slice(UtpPacketHeader.Size));
                    break;
                case UtpPacketType.Fin:
                    ProcessFin(header);
                    break;
                case UtpPacketType.Reset:
                    ProcessReset();
                    break;
                case UtpPacketType.Syn:
                    // SYN should be handled by UtpSocketManager, not here
                    break;
            }

            // Any incoming packet may have freed send-window space (ACK advanced) or grown
            // the peer's advertised window — wake blocked senders to re-evaluate.
            SignalWindowSpace();
        }
    }

    /// <summary>
    /// Periodic tick for retransmission and timeout handling.
    /// Called by UtpSocketManager every ~50ms.
    ///
    /// Mirrors the timeout branch of libtorrent's <c>utp_socket_impl::tick()</c>
    /// (src/utp_stream.cpp): when outstanding packets exceed their RTO we collapse the
    /// congestion window (<c>OnTimeout</c>, == libtorrent resetting cwnd to 1 MSS + slow
    /// start), mark them for resend, and retransmit. The RTO derives from the smoothed
    /// RTT/variance in <see cref="LedbatCongestionControl"/> (== libtorrent
    /// <c>packet_timeout() = max(min_timeout, rtt.mean + 2*avg_deviation)</c>), with
    /// per-packet exponential backoff by transmission count (== libtorrent's
    /// <c>(1 &lt;&lt; (num_timeouts-1)) * 1000</c> escalation). Karn's algorithm holds
    /// because RTT is only sampled from first-send packets in <see cref="ProcessAck"/>.
    /// Deviation from libtorrent: it resends only the earliest packet per timeout and
    /// pumps the rest as the window opens; we resend all currently-outstanding packets
    /// (Go-Back-N) because this codebase has no separate window-driven send pump.
    /// </summary>
    public void Tick()
    {
        // A tick is best-effort. It runs on a Timer/ThreadPool thread, so any escaping
        // exception (e.g. a send on a socket the peer/test just disposed) would crash the
        // whole process. Never let one out.
        try
        {
            TickCore();
        }
        catch
        {
            // swallow — retransmission/window-update failures self-heal on the next tick
        }
    }

    private void TickCore()
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (State is UtpConnectionState.Closed or UtpConnectionState.Reset) return;

            // Receive-side: flush any packets that were held under backpressure now that the
            // app reader may have drained the channel (no incoming packet would otherwise
            // re-trigger reassembly), then send an unsolicited window update if our receive
            // window has reopened from (near) closed — this releases a peer that is blocked
            // on flow control. Mirrors a TCP zero-window-update ACK.
            if (State is UtpConnectionState.Connected or UtpConnectionState.SynRecv)
            {
                TryDeliverReassembled();
                uint wnd = AdvertisedWindow();
                if (wnd > _lastAdvertisedWindow && _lastAdvertisedWindow < (uint)MaxPayloadSize)
                    SendControl(UtpPacketType.State);
            }

            if (_sendBuffer.Count == 0)
            {
                _rtoDeadlineMs = 0; // nothing outstanding — disarm the RTO timer
                return;
            }

            if (_rtoDeadlineMs == 0)
                _rtoDeadlineMs = Environment.TickCount64 + RtoMs();

            if (Environment.TickCount64 < _rtoDeadlineMs)
                return; // RTO not yet expired

            // RTO fired. Retransmit ONLY the oldest unacked packet — the head-of-line gap the
            // receiver is stuck on (== libtorrent tick() resending m_outbuf.at(m_acked_seq_nr+1)).
            // Keep re-sending that same packet every RTO until it is acked; the socket-level
            // deadline (reset only on an advancing ACK) means we stay focused on the gap rather
            // than cycling through already-delivered packets. Resending the whole window
            // (Go-Back-N) overruns the OS UDP send buffer and drops the gap packet itself.
            _resendScratch.Clear();
            _sendBuffer.CollectUnacked(_resendScratch); // sorted ascending by seq
            if (_resendScratch.Count == 0)
            {
                _rtoDeadlineMs = 0;
                return;
            }

            var oldest = _resendScratch[0];
            if (oldest.SendCount > MaxDataResends)
            {
                FailConnection(new TimeoutException(
                    "uTP connection timed out (max retransmissions exceeded)"));
                return;
            }

            // Refresh the volatile header fields (timestamps, advertised window, ack_nr) in
            // place; seq_nr is preserved so the receiver treats it as the same packet.
            PatchHeaderForResend(oldest.Data);
            _sendBuffer.IncrementSendCount(oldest.SeqNr, UtpTimestamp.Now());
            TrySendDatagram(oldest.Data);

            _numTimeouts++;
            _congestion.OnTimeout();                 // congestion collapse on loss
            _rtoDeadlineMs = Environment.TickCount64 + RtoMs(); // back off + re-arm
        }
    }

    /// <summary>Retransmission timeout in ms: smoothed RTT-derived base, floored, with
    /// exponential backoff per consecutive timeout (== libtorrent packet_timeout()). Call under lock.</summary>
    private long RtoMs()
    {
        long baseMs = Math.Max(_congestion.GetTimeoutMs(), 500);
        return baseMs << Math.Min(_numTimeouts, 6); // cap backoff at 64x
    }

    // ---- Internal state machine handlers (all called with _lock held) ----

    private void ProcessState(UtpPacketHeader header)
    {
        // ST_STATE = ACK packet (no payload)
        if (State == UtpConnectionState.SynSent)
        {
            // SYN-ACK received — transition to Connected.
            // ST_STATE does NOT consume a sequence number (unlike ST_SYN/ST_DATA), so the
            // responder's first ST_DATA reuses this same seq_nr. Expect it as-is, not +1.
            _ackNr = (ushort)(header.SequenceNumber - 1);
            _nextExpectedSeqNr = header.SequenceNumber;
            State = UtpConnectionState.Connected;
            _connectTcs.TrySetResult();
        }

        // Process ACK for any state
        ProcessAck(header);
    }

    private void ProcessData(UtpPacketHeader header, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0) return;

        if (State == UtpConnectionState.SynRecv)
        {
            // First data after SYN — transition to Connected
            State = UtpConnectionState.Connected;
        }

        ushort seqNr = header.SequenceNumber;

        // Ignore duplicates/old packets we've already delivered (retransmits of acked data).
        if (PacketBuffer.IsLessWrap(seqNr, _nextExpectedSeqNr))
        {
            // Already consumed — re-ACK so the sender can advance, then drop.
            ProcessAck(header);
            SendControl(UtpPacketType.State);
            return;
        }

        // Store in receive buffer (idempotent for out-of-order retransmits)
        _recvBuffer.Insert(seqNr, payload.ToArray(), payload.Length, 0);

        // Process any ACKs piggybacked on data
        ProcessAck(header);

        // Try to deliver in-order data to the reassembly channel
        TryDeliverReassembled();

        // Send ACK
        SendControl(UtpPacketType.State);
    }

    private void ProcessFin(UtpPacketHeader header)
    {
        // Mirrors libtorrent utp_socket_impl::incoming_packet ST_FIN handling: record the
        // eof sequence number, ACK the FIN, and only signal EOF once all data up to it has
        // been delivered (the FIN may have overtaken a still-missing data packet).
        ProcessAck(header);

        if (!_finReceived)
        {
            _finReceived = true;
            _finSeqNr = header.SequenceNumber;
        }

        // Deliver any already-buffered in-order data; this also runs the FIN-complete check.
        TryDeliverReassembled();

        // ACK the FIN so the closing peer can finish its teardown.
        SendControl(UtpPacketType.State);
    }

    /// <summary>
    /// If a FIN was received and every data packet up to its sequence number has been
    /// delivered, signal EOF to the reader and move to Closed. Call with <see cref="_lock"/> held.
    /// </summary>
    private void CheckFinComplete()
    {
        if (!_finReceived) return;
        // _nextExpectedSeqNr has advanced to (or past) the FIN's seq → all data delivered.
        if (!PacketBuffer.IsLessWrap(_nextExpectedSeqNr, _finSeqNr))
        {
            _ackNr = _finSeqNr;
            State = UtpConnectionState.Closed;
            _reassemblyChannel.Writer.TryComplete();
        }
    }

    private void ProcessReset()
    {
        State = UtpConnectionState.Reset;
        _connectTcs.TrySetException(new InvalidOperationException("Connection reset by peer"));
        _reassemblyChannel.Writer.TryComplete();
    }

    private void FailConnection(Exception error)
    {
        State = UtpConnectionState.Reset;
        _connectTcs.TrySetException(error);
        _reassemblyChannel.Writer.TryComplete();
    }

    private void ProcessAck(UtpPacketHeader header)
    {
        ushort ackNr = header.AckNumber;

        // uTP ack_nr is CUMULATIVE: it acknowledges every packet with sequence number up to
        // and including ack_nr. Remove all such packets from the send buffer and free their
        // window. (The previous implementation only scanned a fixed 100-packet lookback from
        // ack_nr, so a coalesced ACK that jumped forward by more than 100 packets stranded
        // older acked packets in the buffer — _curWindow never drained and the send window
        // stayed permanently full, stalling sustained transfers.)
        _ackScratch.Clear();
        _sendBuffer.CollectUnacked(_ackScratch);
        bool removedAny = false;
        foreach (var entry in _ackScratch)
        {
            ushort seq = entry.SeqNr;
            // seq <= ackNr in wrap-aware terms
            if (seq != ackNr && !PacketBuffer.IsLessWrap(seq, ackNr))
                continue;

            removedAny = true;
            _curWindow -= entry.Data.Length;

            // RTT sample (Karn's algorithm: only from first-send packets)
            if (entry.SendCount == 1 && entry.SentTimestampUs > 0)
            {
                long rttUs = UtpTimestamp.Now() - entry.SentTimestampUs;
                if (rttUs > 0)
                    _congestion.UpdateRtt(rttUs);
            }

            _congestion.OnAck(entry.PayloadLength, Math.Max(_curWindow, 1),
                _lastTimestampDifference);

            _sendBuffer.Remove(seq);
        }

        if (_curWindow < 0) _curWindow = 0;

        if (removedAny)
        {
            // Forward progress — clear the timeout backoff and re-arm the RTO timer for the
            // remaining outstanding data (== libtorrent resetting m_num_timeouts and m_timeout
            // on an ACK). Disarm entirely once everything is acknowledged.
            _numTimeouts = 0;
            _rtoDeadlineMs = _sendBuffer.Count > 0 ? Environment.TickCount64 + RtoMs() : 0;
        }
    }

    private void TryDeliverReassembled()
    {
        // Deliver packets in sequence order. If the reassembly channel is full (the app
        // reader is behind), STOP — leave the packet in the recv buffer and do not advance.
        // This applies real receive-side backpressure instead of silently dropping data:
        // our advertised window shrinks toward zero, throttling the peer (BEP 29 flow
        // control). Delivery resumes from ProcessData (next packet) or Tick (reader drained).
        while (_recvBuffer.TryGet(_nextExpectedSeqNr, out var entry))
        {
            if (entry.Data != null && entry.PayloadLength > 0)
            {
                if (!_reassemblyChannel.Writer.TryWrite(entry.Data))
                    break; // channel full — backpressure
            }

            _recvBuffer.Remove(_nextExpectedSeqNr);
            _ackNr = _nextExpectedSeqNr;
            _nextExpectedSeqNr++;
        }

        // A FIN may have arrived before the packet that just filled the gap — re-check EOF.
        CheckFinComplete();
    }

    /// <summary>
    /// Fire-and-forget datagram send that never throws. UDP send failures (peer/socket
    /// disposed, unreachable, buffer full) are normal and must not escape the receive-loop
    /// or the retransmission timer — see <see cref="Tick"/>.
    /// </summary>
    private void TrySendDatagram(ReadOnlyMemory<byte> data)
    {
        try { _ = _sendDatagram(data, RemoteEndPoint); }
        catch { /* best-effort */ }
    }

    /// <summary>Build and dispatch a header-only control packet (SYN / STATE / FIN / RESET).</summary>
    private void SendControl(UtpPacketType type)
    {
        uint wnd = AdvertisedWindow();
        _lastAdvertisedWindow = wnd;
        var buffer = new byte[UtpPacketHeader.Size];
        var header = new UtpPacketHeader(
            type: type,
            connectionId: type == UtpPacketType.Syn ? RecvConnectionId : SendConnectionId,
            timestampMicroseconds: UtpTimestamp.Now(),
            timestampDifferenceMicroseconds: _lastTimestampDifference,
            windowSize: wnd,
            sequenceNumber: _seqNr,
            ackNumber: _ackNr);

        header.WriteTo(buffer);
        TrySendDatagram(buffer);
    }

    private async Task SendDataPacketAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var packetSize = UtpPacketHeader.Size + payload.Length;
        byte[] datagram;

        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_disposed || State is UtpConnectionState.Closed or UtpConnectionState.Reset)
                    throw new InvalidOperationException($"Cannot send in state {State}");

                if (CanSendNow(packetSize))
                {
                    uint wnd = AdvertisedWindow();
                    _lastAdvertisedWindow = wnd;
                    var header = new UtpPacketHeader(
                        type: UtpPacketType.Data,
                        connectionId: SendConnectionId,
                        timestampMicroseconds: UtpTimestamp.Now(),
                        timestampDifferenceMicroseconds: _lastTimestampDifference,
                        windowSize: wnd,
                        sequenceNumber: _seqNr,
                        ackNumber: _ackNr);

                    datagram = new byte[packetSize];
                    header.WriteTo(datagram);
                    payload.CopyTo(datagram.AsMemory(UtpPacketHeader.Size));

                    // Store the full packet bytes for retransmission (Tick resends this
                    // verbatim, re-patching only the volatile header fields).
                    _sendBuffer.Insert(_seqNr, datagram, payload.Length, UtpTimestamp.Now());
                    _curWindow += packetSize;
                    _seqNr++;

                    // Arm the RTO timer if it isn't already running for outstanding data.
                    if (_rtoDeadlineMs == 0)
                        _rtoDeadlineMs = Environment.TickCount64 + RtoMs();
                    break;
                }

                // Window full — capture the current signal and wait for an ACK / window update.
                wait = _windowSignal.Task;
            }

            await wait.WaitAsync(ct).ConfigureAwait(false);
        }

        await _sendDatagram(datagram, RemoteEndPoint).ConfigureAwait(false);
    }

    /// <summary>
    /// Receiver-advertised window: remaining reassembly-channel capacity in bytes.
    /// Shrinks as the reader falls behind so the peer throttles (BEP 29 wnd_size).
    /// </summary>
    private uint AdvertisedWindow()
    {
        if (!_reassemblyChannel.Reader.CanCount)
            return 65536;
        int free = Math.Max(0, 256 - _reassemblyChannel.Reader.Count);
        return (uint)(free * MaxPayloadSize);
    }

    /// <summary>
    /// Refresh the volatile header fields of a stored packet before retransmission:
    /// timestamp, timestamp-difference, advertised window and ack_nr. The type, connection
    /// id and (critically) seq_nr are left untouched so the receiver treats the resend as
    /// the same logical packet. Mirrors libtorrent re-stamping the header in send_pkt().
    /// </summary>
    private void PatchHeaderForResend(byte[] packet)
    {
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), UtpTimestamp.Now());
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), _lastTimestampDifference);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), AdvertisedWindow());
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(18), _ackNr);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;

            // Graceful teardown: on a live connection, notify the peer with ST_FIN so it
            // learns of the close immediately (rather than via reset/timeout). Mirrors
            // libtorrent send_fin(). We then move straight to Closed — this is a synchronous
            // hard dispose, so we don't linger in FIN-SENT awaiting the peer's ack/FIN.
            // NOTE: any data still unacked at this point is not retransmitted after dispose;
            // callers should drain the stream before closing on lossy paths.
            if (State is UtpConnectionState.Connected or UtpConnectionState.SynRecv
                or UtpConnectionState.FinSent)
            {
                SendFin();
            }

            State = UtpConnectionState.Closed;
            _disposed = true;
            _reassemblyChannel.Writer.TryComplete();
            _connectTcs.TrySetCanceled();
            // Wake any sender blocked on the flow-control window so it observes the close.
            SignalWindowSpace();
        }
    }

    /// <summary>Send ST_FIN. The FIN consumes a sequence number (like ST_DATA). Call with <see cref="_lock"/> held.</summary>
    private void SendFin()
    {
        uint wnd = AdvertisedWindow();
        _lastAdvertisedWindow = wnd;
        var buffer = new byte[UtpPacketHeader.Size];
        var header = new UtpPacketHeader(
            type: UtpPacketType.Fin,
            connectionId: SendConnectionId,
            timestampMicroseconds: UtpTimestamp.Now(),
            timestampDifferenceMicroseconds: _lastTimestampDifference,
            windowSize: wnd,
            sequenceNumber: _seqNr,
            ackNumber: _ackNr);
        header.WriteTo(buffer);
        _seqNr++; // FIN consumes a sequence number
        TrySendDatagram(buffer);
    }
}
