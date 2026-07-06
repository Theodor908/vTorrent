using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Microsoft.Extensions.Logging;
using vTorrent.Core.PeerCommunication.Utilities;
using vTorrent.Core.PieceIO;
using vTorrent.Core.Upload;
using Xunit;

namespace vTorrent.Core.Tests.Upload;

public class SeedModeVerifierTests
{
    private readonly Mock<IPieceManager> _pieceManagerMock = new();
    private readonly ILogger<SeedModeVerifier> _logger = new LoggerFactory().CreateLogger<SeedModeVerifier>();

    private SeedModeVerifier CreateVerifier(int pieceCount, byte[][] pieceHashes)
    {
        var verifiedPieces = new Bitfield(pieceCount);
        return new SeedModeVerifier(verifiedPieces, _pieceManagerMock.Object, pieceHashes, pieceCount, _logger);
    }

    [Fact]
    public void IsVerified_ReturnsFalse_ForUnverifiedPiece()
    {
        var verifier = CreateVerifier(10, new byte[10][]);
        verifier.IsVerified(0).Should().BeFalse();
        verifier.IsVerified(9).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyPieceAsync_ReturnsVerified_WhenHashMatches()
    {
        var pieceData = new byte[] { 1, 2, 3, 4, 5 };
        var expectedHash = System.Security.Cryptography.SHA1.HashData(pieceData);
        var hashes = new byte[1][];
        hashes[0] = expectedHash;

        _pieceManagerMock
            .Setup(m => m.ReadPieceAsync(0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PieceReadResult.Success(0, pieceData, false));

        var verifier = CreateVerifier(1, hashes);
        var result = await verifier.VerifyPieceAsync(0, CancellationToken.None);

        result.Should().Be(SeedVerifyResult.Verified);
        verifier.IsVerified(0).Should().BeTrue();
    }

    [Fact]
    public async Task VerifyPieceAsync_ReturnsFailed_WhenHashMismatches()
    {
        var pieceData = new byte[] { 1, 2, 3, 4, 5 };
        var wrongHash = new byte[20];
        var hashes = new byte[1][];
        hashes[0] = wrongHash;

        _pieceManagerMock
            .Setup(m => m.ReadPieceAsync(0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PieceReadResult.Success(0, pieceData, false));

        bool abortFired = false;
        var verifier = CreateVerifier(1, hashes);
        verifier.SeedModeAborted += (_, _) => abortFired = true;

        var result = await verifier.VerifyPieceAsync(0, CancellationToken.None);

        result.Should().Be(SeedVerifyResult.Failed);
        abortFired.Should().BeTrue();
        verifier.IsVerified(0).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyPieceAsync_SkipsHash_WhenAlreadyVerified()
    {
        var pieceData = new byte[] { 1, 2, 3, 4, 5 };
        var hash = System.Security.Cryptography.SHA1.HashData(pieceData);
        var hashes = new byte[1][];
        hashes[0] = hash;

        _pieceManagerMock
            .Setup(m => m.ReadPieceAsync(0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PieceReadResult.Success(0, pieceData, false));

        var verifier = CreateVerifier(1, hashes);
        await verifier.VerifyPieceAsync(0, CancellationToken.None);

        _pieceManagerMock.Invocations.Clear();
        var result = await verifier.VerifyPieceAsync(0, CancellationToken.None);

        result.Should().Be(SeedVerifyResult.Verified);
        _pieceManagerMock.Verify(m => m.ReadPieceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyPieceAsync_DeduplicatesConcurrentCalls()
    {
        var pieceData = new byte[] { 1, 2, 3 };
        var hash = System.Security.Cryptography.SHA1.HashData(pieceData);
        var hashes = new byte[1][];
        hashes[0] = hash;

        var tcs = new TaskCompletionSource<PieceReadResult>();
        _pieceManagerMock
            .Setup(m => m.ReadPieceAsync(0, It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var verifier = CreateVerifier(1, hashes);

        var task1 = verifier.VerifyPieceAsync(0, CancellationToken.None);
        var task2 = verifier.VerifyPieceAsync(0, CancellationToken.None);

        tcs.SetResult(PieceReadResult.Success(0, pieceData, false));

        var result1 = await task1;
        var result2 = await task2;

        result1.Should().Be(SeedVerifyResult.Verified);
        result2.Should().Be(SeedVerifyResult.Verified);
        _pieceManagerMock.Verify(m => m.ReadPieceAsync(0, It.IsAny<CancellationToken>()), Times.Once);
    }
}
