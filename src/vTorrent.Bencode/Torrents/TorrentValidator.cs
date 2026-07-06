using System;

namespace vTorrent.Bencode.Torrents;

/// <summary>
/// Validates torrent metadata against BEP 3 (v1) and BEP 52 (v2) constraints.
/// </summary>
public static class TorrentValidator
{
    public static void Validate(Torrent torrent)
    {
        if (torrent is null) throw new ArgumentNullException(nameof(torrent));

        torrent.Info.Validate();

        if (torrent.Info.Version is TorrentVersion.V2 or TorrentVersion.Hybrid)
            ValidateV2(torrent);
    }

    private static void ValidateV2(Torrent torrent)
    {
        var pieceLength = torrent.Info.PieceLength;

        if (pieceLength < 16384)
            throw new InvalidOperationException(
                $"v2 piece length must be >= 16 KiB (16384 bytes), got {pieceLength}");

        if ((pieceLength & (pieceLength - 1)) != 0)
            throw new InvalidOperationException(
                $"v2 piece length must be a power of 2, got {pieceLength}");

        if (torrent.Info.FileTreeV2 is null)
            throw new InvalidOperationException("v2 torrent must have a file tree");
    }
}
