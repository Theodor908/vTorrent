using FluentAssertions;
using Moq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Storage;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.PieceIO;
using Xunit;

namespace vTorrent.Tests.Unit.PieceIO;

public class PieceVerificationPipelineTests
{
    private const int PieceCount = 10;
    private const int PieceSize = 16384; // 16 KiB

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Generates deterministic piece data: each piece i is filled with byte value (i + 1).
    /// </summary>
    private static byte[][] BuildPieceData()
    {
        var data = new byte[PieceCount][];
        for (int i = 0; i < PieceCount; i++)
        {
            data[i] = new byte[PieceSize];
            Array.Fill(data[i], (byte)(i + 1));
        }
        return data;
    }

    /// <summary>
    /// Computes SHA1 hashes for each piece and returns a PieceHashes object.
    /// </summary>
    private static PieceHashes BuildPieceHashes(byte[][] pieceData)
    {
        var allHashes = new byte[PieceCount * 20];
        for (int i = 0; i < PieceCount; i++)
        {
            var hash = SHA1.HashData(pieceData[i]);
            hash.CopyTo(allHashes, i * 20);
        }
        return new PieceHashes(allHashes);
    }

    /// <summary>
    /// Builds the TorrentInfo for a single-file v1 torrent of PieceCount * PieceSize bytes.
    /// </summary>
    private static TorrentInfo BuildTorrentInfo(PieceHashes pieces)
    {
        long totalSize = (long)PieceCount * PieceSize;
        return new TorrentInfo
        {
            Name = "test",
            PieceLength = PieceSize,
            Pieces = pieces,
            Files = new[]
            {
                new TorrentFile
                {
                    Path = new[] { "test" },
                    Length = totalSize
                }
            }
        };
    }

    /// <summary>
    /// Creates a mock IDiskBackend that serves pieceData[i] when reading at offset i * PieceSize.
    /// </summary>
    private static Mock<IDiskBackend> BuildBackendMock(byte[][] pieceData)
    {
        var backend = new Mock<IDiskBackend>();

        backend
            .Setup(b => b.ReadAsync(
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<Memory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, long, Memory<byte>, CancellationToken>((_, offset, buf, _) =>
            {
                var pieceIndex = (int)(offset / PieceSize);
                if (pieceIndex >= 0 && pieceIndex < PieceCount)
                {
                    pieceData[pieceIndex].AsMemory(0, buf.Length).CopyTo(buf);
                    return ValueTask.FromResult(buf.Length);
                }
                return ValueTask.FromResult(0);
            });

        backend
            .Setup(b => b.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        return backend;
    }

    /// <summary>
    /// Creates a fully wired PieceVerificationPipeline.
    /// </summary>
    private static PieceVerificationPipeline BuildPipeline(IDiskBackend backend, TorrentInfo info)
    {
        var verifier = new PieceVerifier(info);
        var mapper = new PieceMapper("/fake", info);
        // checkingMemUsageBlocks = 64 (1 MiB), hashThreads = 2
        return new PieceVerificationPipeline(backend, verifier, mapper, PieceCount, 64, 2);
    }

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AllPiecesValid_AllBitsSet()
    {
        var pieceData = BuildPieceData();
        var pieces = BuildPieceHashes(pieceData);
        var info = BuildTorrentInfo(pieces);
        var backend = BuildBackendMock(pieceData).Object;
        var pipeline = BuildPipeline(backend, info);

        var result = await pipeline.VerifyAllPiecesAsync(
            progress: null,
            startPiece: 0,
            skipPieces: null,
            ct: CancellationToken.None);

        for (int i = 0; i < PieceCount; i++)
            result[i].Should().BeTrue(because: $"piece {i} has correct data");
    }

    [Fact]
    public async Task StartPiece_5_PiecesBefore5AreFalse()
    {
        var pieceData = BuildPieceData();
        var pieces = BuildPieceHashes(pieceData);
        var info = BuildTorrentInfo(pieces);
        var backend = BuildBackendMock(pieceData).Object;
        var pipeline = BuildPipeline(backend, info);

        var result = await pipeline.VerifyAllPiecesAsync(
            progress: null,
            startPiece: 5,
            skipPieces: null,
            ct: CancellationToken.None);

        // Pieces 0-4 were never read → should be false
        for (int i = 0; i < 5; i++)
            result[i].Should().BeFalse(because: $"piece {i} was not verified (before startPiece=5)");

        // Pieces 5-9 were read and verified → should be true
        for (int i = 5; i < PieceCount; i++)
            result[i].Should().BeTrue(because: $"piece {i} has correct data and was verified");
    }

    [Fact]
    public async Task SkipPieces_SkippedBitsFalse_RestTrue()
    {
        var pieceData = BuildPieceData();
        var pieces = BuildPieceHashes(pieceData);
        var info = BuildTorrentInfo(pieces);
        var backend = BuildBackendMock(pieceData).Object;
        var pipeline = BuildPipeline(backend, info);

        var skipSet = new HashSet<int> { 2, 5, 7 };

        var result = await pipeline.VerifyAllPiecesAsync(
            progress: null,
            startPiece: 0,
            skipPieces: skipSet,
            ct: CancellationToken.None);

        for (int i = 0; i < PieceCount; i++)
        {
            if (skipSet.Contains(i))
                result[i].Should().BeFalse(because: $"piece {i} was skipped");
            else
                result[i].Should().BeTrue(because: $"piece {i} has correct data");
        }
    }

    [Fact]
    public async Task ProgressCallback_ReportedForEachVerifiedPiece()
    {
        var pieceData = BuildPieceData();
        var pieces = BuildPieceHashes(pieceData);
        var info = BuildTorrentInfo(pieces);
        var backend = BuildBackendMock(pieceData).Object;
        var pipeline = BuildPipeline(backend, info);

        var reported = new List<PieceVerificationPipeline.VerificationProgress>();
        var progress = new Progress<PieceVerificationPipeline.VerificationProgress>(p =>
        {
            lock (reported) reported.Add(p);
        });

        await pipeline.VerifyAllPiecesAsync(
            progress: progress,
            startPiece: 0,
            skipPieces: null,
            ct: CancellationToken.None);

        // Progress is reported asynchronously via Progress<T> which posts to sync context.
        // Give it a brief moment to flush.
        await Task.Delay(50);

        reported.Should().HaveCount(PieceCount);

        for (int i = 0; i < PieceCount; i++)
        {
            reported.Should().ContainSingle(
                p => p.PieceIndex == i,
                because: $"piece {i} should have been reported");
        }
    }

    [Fact]
    public async Task StartPieceAndSkip_Combined()
    {
        var pieceData = BuildPieceData();
        var pieces = BuildPieceHashes(pieceData);
        var info = BuildTorrentInfo(pieces);
        var backend = BuildBackendMock(pieceData).Object;
        var pipeline = BuildPipeline(backend, info);

        // Start at 3, skip {4, 6}
        var skipSet = new HashSet<int> { 4, 6 };

        var result = await pipeline.VerifyAllPiecesAsync(
            progress: null,
            startPiece: 3,
            skipPieces: skipSet,
            ct: CancellationToken.None);

        // Before startPiece → false
        for (int i = 0; i < 3; i++)
            result[i].Should().BeFalse(because: $"piece {i} is before startPiece=3");

        // Pieces >= 3 but in skipSet → false
        result[4].Should().BeFalse(because: "piece 4 is in skipPieces");
        result[6].Should().BeFalse(because: "piece 6 is in skipPieces");

        // Remaining pieces (3, 5, 7, 8, 9) → true
        foreach (int i in new[] { 3, 5, 7, 8, 9 })
            result[i].Should().BeTrue(because: $"piece {i} has correct data");
    }
}
