using System.Buffers;

namespace vTorrent.Core.Download;

/// <summary>
/// Thin wrapper around ArrayPool for 16KB block buffers.
/// Centralizes buffer lifecycle for the download pipeline.
/// </summary>
public static class BlockBufferPool
{
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

    public static byte[] Rent(int minimumLength = 16384)
        => Pool.Rent(minimumLength);

    public static void Return(byte[] buffer, bool clearArray = false)
    {
        if (buffer != null)
            Pool.Return(buffer, clearArray);
    }
}
