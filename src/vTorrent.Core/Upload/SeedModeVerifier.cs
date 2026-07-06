using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Core.PeerCommunication.Utilities;
using vTorrent.Core.PieceIO;

namespace vTorrent.Core.Upload;

public enum SeedVerifyResult
{
    Verified,
    Failed
}

public sealed class SeedModeVerifier
{
    private readonly Bitfield _verifiedPieces;
    private readonly IPieceManager _pieceManager;
    private readonly byte[][] _pieceHashes;
    private readonly int _pieceCount;
    private readonly ILogger<SeedModeVerifier> _logger;
    private readonly ConcurrentDictionary<int, Task<SeedVerifyResult>> _pendingVerifications = new();

    public event EventHandler? SeedModeAborted;

    public SeedModeVerifier(
        Bitfield verifiedPieces,
        IPieceManager pieceManager,
        byte[][] pieceHashes,
        int pieceCount,
        ILogger<SeedModeVerifier> logger)
    {
        _verifiedPieces = verifiedPieces ?? throw new ArgumentNullException(nameof(verifiedPieces));
        _pieceManager = pieceManager ?? throw new ArgumentNullException(nameof(pieceManager));
        _pieceHashes = pieceHashes ?? throw new ArgumentNullException(nameof(pieceHashes));
        _pieceCount = pieceCount;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsVerified(int pieceIndex) => _verifiedPieces.HasPiece(pieceIndex);

    public Task<SeedVerifyResult> VerifyPieceAsync(int pieceIndex, CancellationToken ct)
    {
        if (_verifiedPieces.HasPiece(pieceIndex))
            return Task.FromResult(SeedVerifyResult.Verified);

        return _pendingVerifications.GetOrAdd(pieceIndex, idx => VerifyPieceInternalAsync(idx, ct));
    }

    private async Task<SeedVerifyResult> VerifyPieceInternalAsync(int pieceIndex, CancellationToken ct)
    {
        try
        {
            var readResult = await _pieceManager.ReadPieceAsync(pieceIndex, ct).ConfigureAwait(false);

            if (!readResult.IsSuccess)
            {
                _logger.LogWarning("Seed mode: failed to read piece {Piece} for verification: {Error}",
                    pieceIndex, readResult.ErrorMessage);
                SeedModeAborted?.Invoke(this, EventArgs.Empty);
                return SeedVerifyResult.Failed;
            }

            var actualHash = SHA1.HashData(readResult.Data);
            var expectedHash = _pieceHashes[pieceIndex];

            if (expectedHash == null || !actualHash.AsSpan().SequenceEqual(expectedHash))
            {
                _logger.LogWarning("Seed mode ABORTED: piece {Piece} failed hash verification", pieceIndex);
                SeedModeAborted?.Invoke(this, EventArgs.Empty);
                return SeedVerifyResult.Failed;
            }

            _verifiedPieces.SetPiece(pieceIndex);
            _logger.LogDebug("Seed mode: piece {Piece} verified on upload", pieceIndex);
            return SeedVerifyResult.Verified;
        }
        finally
        {
            _pendingVerifications.TryRemove(pieceIndex, out _);
        }
    }
}
