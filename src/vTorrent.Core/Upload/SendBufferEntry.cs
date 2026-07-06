namespace vTorrent.Core.Upload;

/// <summary>
/// A pre-read 16 KiB block sitting in a peer's send buffer, waiting to be served.
/// Data is rented from ArrayPool&lt;byte&gt;.Shared and must be returned after use.
/// </summary>
internal readonly record struct SendBufferEntry(
    int PieceIndex,
    int Begin,
    byte[] Data,    // rented from ArrayPool<byte>.Shared
    int Length);     // actual data length (≤ Data.Length due to pool rounding)
