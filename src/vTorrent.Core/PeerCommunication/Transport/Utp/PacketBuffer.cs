using System.Collections.Generic;

namespace vTorrent.Core.PeerCommunication.Transport.Utp;

public sealed class PacketBuffer
{
    private readonly PacketEntry?[] _entries;
    private readonly int _mask;

    public int Count { get; private set; }

    public PacketBuffer(int capacity)
    {
        int size = 1;
        while (size < capacity) size <<= 1;
        _entries = new PacketEntry?[size];
        _mask = size - 1;
    }

    public void Insert(ushort seqNr, byte[] data, int payloadLength, long sentTimestampUs)
    {
        int index = seqNr & _mask;
        if (_entries[index] == null) Count++;
        _entries[index] = new PacketEntry(seqNr, data, payloadLength, sentTimestampUs, 1, false);
    }

    public bool TryGet(ushort seqNr, out PacketEntry entry)
    {
        int index = seqNr & _mask;
        var e = _entries[index];
        if (e != null && e.Value.SeqNr == seqNr)
        {
            entry = e.Value;
            return true;
        }
        entry = default;
        return false;
    }

    public void MarkAcked(ushort seqNr)
    {
        int index = seqNr & _mask;
        var e = _entries[index];
        if (e != null && e.Value.SeqNr == seqNr)
        {
            var v = e.Value;
            _entries[index] = new PacketEntry(v.SeqNr, v.Data, v.PayloadLength,
                v.SentTimestampUs, v.SendCount, true);
        }
    }

    public void IncrementSendCount(ushort seqNr, long newSentTimestampUs)
    {
        int index = seqNr & _mask;
        var e = _entries[index];
        if (e != null && e.Value.SeqNr == seqNr)
        {
            var v = e.Value;
            _entries[index] = new PacketEntry(v.SeqNr, v.Data, v.PayloadLength,
                newSentTimestampUs, v.SendCount + 1, v.Acked);
        }
    }

    public void Remove(ushort seqNr)
    {
        int index = seqNr & _mask;
        var e = _entries[index];
        if (e != null && e.Value.SeqNr == seqNr)
        {
            _entries[index] = null;
            Count--;
        }
    }

    /// <summary>
    /// Appends every currently-buffered, not-yet-acked packet to <paramref name="into"/>,
    /// ordered by ascending sequence number. Used by the retransmission walk in
    /// <c>UtpSocket.Tick</c> to snapshot outstanding packets before mutating the buffer.
    /// </summary>
    public void CollectUnacked(List<PacketEntry> into)
    {
        // Snapshot into a caller-owned list so the caller can resend / IncrementSendCount
        // without mutating the underlying array mid-iteration.
        int start = into.Count;
        for (int i = 0; i < _entries.Length; i++)
        {
            var e = _entries[i];
            if (e != null && !e.Value.Acked)
                into.Add(e.Value);
        }
        // Deliver in ascending wrap-aware sequence order so the earliest lost packet is
        // resent first (matches libtorrent's m_acked_seq_nr+1 forward walk in tick()).
        into.Sort(start, into.Count - start, UnackedSeqComparer.Instance);
    }

    private sealed class UnackedSeqComparer : IComparer<PacketEntry>
    {
        public static readonly UnackedSeqComparer Instance = new();
        public int Compare(PacketEntry a, PacketEntry b)
            => IsLessWrap(a.SeqNr, b.SeqNr) ? -1 : (a.SeqNr == b.SeqNr ? 0 : 1);
    }

    /// <summary>
    /// Wrapping less-than for 16-bit sequence numbers.
    /// Returns true if lhs is "before" rhs in the circular sequence space.
    /// </summary>
    public static bool IsLessWrap(ushort lhs, ushort rhs)
    {
        return (short)(lhs - rhs) < 0;
    }
}

public readonly record struct PacketEntry(
    ushort SeqNr,
    byte[] Data,
    int PayloadLength,
    long SentTimestampUs,
    int SendCount,
    bool Acked);
