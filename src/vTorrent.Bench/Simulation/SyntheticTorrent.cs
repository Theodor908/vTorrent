using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using vTorrent.Bencode.Torrents;

namespace vTorrent.Bench.Simulation;

public sealed class SyntheticTorrent
{
    public TorrentInfo Info { get; }
    public Torrent Torrent { get; }
    public IReadOnlyList<byte[]> PieceData { get; }

    private SyntheticTorrent(TorrentInfo info, Torrent torrent, IReadOnlyList<byte[]> pieceData)
    {
        Info = info;
        Torrent = torrent;
        PieceData = pieceData;
    }

    public static SyntheticTorrent Generate(int pieceCount, int pieceSize)
    {
        var allPieceData = new byte[pieceCount][];
        var hashBytes = new byte[pieceCount * PieceHashes.HashSize];

        using var sha1 = SHA1.Create();

        for (int i = 0; i < pieceCount; i++)
        {
            allPieceData[i] = GeneratePieceData(i, pieceSize);
            var hash = sha1.ComputeHash(allPieceData[i]);
            Buffer.BlockCopy(hash, 0, hashBytes, i * PieceHashes.HashSize, PieceHashes.HashSize);
        }

        long totalSize = (long)pieceCount * pieceSize;

        var torrentInfo = new TorrentInfo
        {
            Name = $"bench-{pieceCount}x{pieceSize}",
            PieceLength = pieceSize,
            Pieces = new PieceHashes(hashBytes),
            Files = new List<TorrentFile>
            {
                new TorrentFile { Path = new[] { "bench-data.bin" }, Length = totalSize }
            }.AsReadOnly()
        };

        var torrent = new Torrent
        {
            Announce = "udp://bench.local:6969/announce",
            Info = torrentInfo,
            CreatedBy = "vTorrent.Bench"
        };

        return new SyntheticTorrent(torrentInfo, torrent, allPieceData);
    }

    public static byte[] GeneratePieceData(int pieceIndex, int pieceSize)
    {
        var data = new byte[pieceSize];
        var rng = new Random(pieceIndex * 31337);
        rng.NextBytes(data);
        return data;
    }

    public byte[] GetBlock(int pieceIndex, int begin, int length)
    {
        var piece = PieceData[pieceIndex];
        var block = new byte[length];
        Buffer.BlockCopy(piece, begin, block, 0, length);
        return block;
    }
}
