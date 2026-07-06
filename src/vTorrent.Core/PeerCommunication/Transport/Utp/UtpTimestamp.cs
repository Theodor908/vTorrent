using System;
using System.Diagnostics;

namespace vTorrent.Core.PeerCommunication.Transport.Utp;

/// <summary>
/// Microsecond-precision monotonic timestamp for uTP delay measurement.
/// Uses Stopwatch (highest resolution clock in .NET) — only relative
/// differences matter per BEP 29, so arbitrary epoch is fine.
/// </summary>
public static class UtpTimestamp
{
    private static readonly long Frequency = Stopwatch.Frequency;

    /// <summary>
    /// Current time as BEP 29 microseconds in a wrapping 32-bit counter.
    /// </summary>
    /// <remarks>
    /// Computed with integer math: <c>ticks * 1_000_000 / frequency</c> via <see cref="Int128"/>
    /// to avoid overflow (ticks*1e6 exceeds <see cref="long"/> range on multi-day uptimes),
    /// then truncated to the low 32 bits. A direct <c>(uint)(double)</c> cast of the large
    /// tick*scale product is OUT OF RANGE and yields 0 on x64 — which silently zeroed every
    /// uTP delay/RTT/RTO measurement. The <c>(uint)</c> of a <c>long</c>/<c>ulong</c> is a
    /// defined modulo-2^32 wrap, which is exactly the counter semantics BEP 29 wants.
    /// </remarks>
    public static uint Now()
    {
        long ticks = Stopwatch.GetTimestamp();
        ulong micros = (ulong)((Int128)ticks * 1_000_000 / Frequency);
        return (uint)micros;
    }
}
