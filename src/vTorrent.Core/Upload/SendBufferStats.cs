namespace vTorrent.Core.Upload;

/// <summary>
/// Diagnostic counters for the send buffer subsystem.
/// Follows the same pattern as <see cref="vTorrent.Abstractions.Storage.DiskBackendStats"/>.
/// </summary>
public record struct SendBufferStats(
    long TotalBufferedBytes,
    long BufferHits,
    long BufferMisses,
    long ReadAheadInvalidations,
    int ActivePeerBuffers,
    PressureState Pressure);

/// <summary>3-tier memory pressure state, same ratios as DiskWriteThrottler.</summary>
public enum PressureState { Normal, SoftPressure, HardPause }
