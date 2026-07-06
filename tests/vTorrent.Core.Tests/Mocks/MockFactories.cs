using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Bencode.Torrents;
using vTorrent.Abstractions.Models;
using vTorrent.Core;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Utilities;
using vTorrent.Core.PieceIO;
using vTorrent.Core.Engine;
using vTorrent.Core.Upload;
using vTorrent.Core.Download;

namespace vTorrent.Tests.Mocks;

/// <summary>
/// Centralized mock factory for creating test doubles consistently across all unit tests.
/// </summary>
public static class MockFactories
{
    private static int _peerCounter = 0;

    #region TorrentInfo

    /// <summary>
    /// Creates a simple TorrentInfo for testing with configurable parameters.
    /// </summary>
    public static TorrentInfo CreateTorrentInfo(
        int pieceCount = 100,
        long pieceLength = 16384,
        string name = "TestTorrent")
    {
        // Create piece hashes (20 bytes each, all zeros for testing)
        var pieceHashData = new byte[pieceCount * 20];
        var pieceHashes = new PieceHashes(pieceHashData);

        // Create single file
        var files = new List<TorrentFile>
        {
            new TorrentFile
            {
                Path = new[] { $"{name}.dat" },
                Length = pieceCount * pieceLength
            }
        };

        return new TorrentInfo
        {
            Name = name,
            PieceLength = pieceLength,
            Pieces = pieceHashes,
            IsPrivate = false,
            Files = files
        };
    }

    /// <summary>
    /// Creates TorrentInfo for multi-file torrents.
    /// </summary>
    public static TorrentInfo CreateMultiFileTorrentInfo(
        int pieceCount = 100,
        long pieceLength = 16384,
        string name = "TestMultiTorrent",
        int fileCount = 3)
    {
        var pieceHashData = new byte[pieceCount * 20];
        var pieceHashes = new PieceHashes(pieceHashData);

        long totalSize = pieceCount * pieceLength;
        long sizePerFile = totalSize / fileCount;

        var files = new List<TorrentFile>();
        for (int i = 0; i < fileCount; i++)
        {
            files.Add(new TorrentFile
            {
                Path = new[] { name, $"file{i + 1}.dat" },
                Length = i < fileCount - 1 ? sizePerFile : totalSize - (sizePerFile * (fileCount - 1))
            });
        }

        return new TorrentInfo
        {
            Name = name,
            PieceLength = pieceLength,
            Pieces = pieceHashes,
            IsPrivate = false,
            Files = files
        };
    }

    #endregion

    #region Peer Mocks

    /// <summary>
    /// Creates a mock IPeerConnection with configurable state.
    /// </summary>
    public static Mock<IPeerConnection> CreatePeerConnectionMock(
        bool isConnected = true,
        bool isChoked = true,
        bool isChoking = true,
        bool isInterested = false,
        bool peerIsInterested = false,
        int pieceCount = 100,
        bool hasPieces = false)
    {
        var mock = new Mock<IPeerConnection>();
        var counter = Interlocked.Increment(ref _peerCounter);
        var ip = IPAddress.Parse($"192.168.1.{counter % 255}");
        var peerInfo = new PeerInfo(ip, 6881 + (counter % 1000));

        mock.Setup(p => p.PeerInfo).Returns(peerInfo);
        mock.Setup(p => p.EndpointString).Returns(peerInfo.EndPoint?.ToString() ?? "");
        mock.Setup(p => p.PeerId).Returns(GeneratePeerId(counter));
        mock.Setup(p => p.IsConnected).Returns(isConnected);
        mock.Setup(p => p.IsChoked).Returns(isChoked);
        mock.Setup(p => p.IsChoking).Returns(isChoking);
        mock.Setup(p => p.IsInterested).Returns(isInterested);
        mock.Setup(p => p.PeerIsInterested).Returns(peerIsInterested);
        mock.Setup(p => p.ConnectedAt).Returns(DateTime.UtcNow);
        mock.Setup(p => p.RoundTripTimeMs).Returns(50);
        mock.Setup(p => p.IsSnubbed).Returns(false);
        mock.Setup(p => p.IsSeed).Returns(hasPieces);
        mock.Setup(p => p.RemoteRequestQueueSize).Returns((int?)null);
        mock.Setup(p => p.BytesDownloaded).Returns(0);
        mock.Setup(p => p.BytesUploaded).Returns(0);

        // Setup bitfield
        var bitfieldBytes = new byte[(pieceCount + 7) / 8];
        if (hasPieces)
        {
            // Mark all pieces as available
            for (int i = 0; i < bitfieldBytes.Length; i++)
            {
                bitfieldBytes[i] = 0xFF;
            }
        }
        mock.Setup(p => p.PeerBitfield).Returns(bitfieldBytes);

        // Setup async methods to complete successfully
        mock.Setup(p => p.ConnectAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(p => p.SendMessageAsync(It.IsAny<PeerMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(p => p.SendMessagesAsync(It.IsAny<IReadOnlyList<PeerMessage>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(p => p.SendBitfieldAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(p => p.SetInterestedAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(p => p.SetChokingAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(p => p.RequestBlockAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(p => p.RequestBlocksBatchAsync(It.IsAny<IReadOnlyList<(int, int, int)>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(p => p.SendBlockAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(p => p.CancelBlockAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(p => p.AnnounceHaveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(p => p.DisconnectAsync())
            .Returns(Task.CompletedTask);

        return mock;
    }

    /// <summary>
    /// Creates a mock IPeerManager with configurable peer list.
    /// </summary>
    public static Mock<IPeerManager> CreatePeerManagerMock(
        List<IPeerConnection> connectedPeers = null,
        int maxConnections = 50)
    {
        var mock = new Mock<IPeerManager>();
        connectedPeers ??= new List<IPeerConnection>();

        mock.Setup(m => m.ConnectedPeers).Returns(connectedPeers);
        mock.Setup(m => m.ConnectedPeerCount).Returns(() => connectedPeers.Count);
        mock.Setup(m => m.MaxConnections).Returns(maxConnections);
        mock.Setup(m => m.InfoHash).Returns(new byte[20]);
        mock.Setup(m => m.TotalBytesDownloaded).Returns(0);
        mock.Setup(m => m.TotalBytesUploaded).Returns(0);

        mock.Setup(m => m.StartAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(m => m.StopAsync())
            .Returns(Task.CompletedTask);
        mock.Setup(m => m.AddPeerAsync(It.IsAny<PeerInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mock.Setup(m => m.AddPeersAsync(It.IsAny<IEnumerable<PeerInfo>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(m => m.RemovePeerAsync(It.IsAny<IPeerConnection>()))
            .Returns(Task.CompletedTask);
        mock.Setup(m => m.BroadcastHaveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(m => m.BroadcastBitfieldAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(m => m.GetPeersWithPiece(It.IsAny<int>()))
            .Returns<int>(pieceIndex => connectedPeers.Where(p => HasPiece(p, pieceIndex)));
        mock.Setup(m => m.GetAvailablePeers())
            .Returns(() => connectedPeers.Where(p => p.IsConnected && !p.IsChoked));
        mock.Setup(m => m.GetInterestedPeers())
            .Returns(() => connectedPeers.Where(p => p.PeerIsInterested));

        return mock;
    }

    private static bool HasPiece(IPeerConnection peer, int pieceIndex)
    {
        if (peer.PeerBitfield == null || peer.PeerBitfield.Length == 0)
            return false;

        int byteIndex = pieceIndex / 8;
        int bitIndex = pieceIndex % 8;

        if (byteIndex >= peer.PeerBitfield.Length)
            return false;

        return (peer.PeerBitfield[byteIndex] & (0x80 >> bitIndex)) != 0;
    }

    private static byte[] GeneratePeerId(int counter)
    {
        var peerId = new byte[20];
        var prefix = "-VT0001-"u8.ToArray();
        Buffer.BlockCopy(prefix, 0, peerId, 0, prefix.Length);
        var suffix = BitConverter.GetBytes(counter);
        Buffer.BlockCopy(suffix, 0, peerId, prefix.Length, Math.Min(suffix.Length, 20 - prefix.Length));
        return peerId;
    }

    #endregion

    #region PieceManager Mocks

    /// <summary>
    /// Creates a mock IPieceManager.
    /// </summary>
    public static Mock<IPieceManager> CreatePieceManagerMock(
        int pieceCount = 100,
        BitArray completedPieces = null)
    {
        var mock = new Mock<IPieceManager>();
        completedPieces ??= new BitArray(pieceCount, false);

        mock.Setup(m => m.CompletedPieceCount).Returns(() => CountTrue(completedPieces));
        mock.Setup(m => m.IsFenced).Returns(false);
        mock.Setup(m => m.GetBitfield()).Returns(completedPieces);

        mock.Setup(m => m.WritePieceAsync(
                It.IsAny<int>(),
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync((int pieceIndex, byte[] data, CancellationToken ct, bool skip) =>
                PieceWriteResult.Success(pieceIndex, data.Length, true));

        mock.Setup(m => m.WritePiece(It.IsAny<int>(), It.IsAny<byte[]>()))
            .Returns((int pieceIndex, byte[] data) =>
                PieceWriteResult.Success(pieceIndex, data.Length, true));

        mock.Setup(m => m.ReadPieceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int pieceIndex, CancellationToken ct) =>
                PieceReadResult.Success(pieceIndex, new byte[16384], true));

        mock.Setup(m => m.ReadBlockAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int pieceIndex, int offset, int length, CancellationToken ct) =>
                PieceReadResult.Success(pieceIndex, new byte[length], true));

        mock.Setup(m => m.ReadPiece(It.IsAny<int>()))
            .Returns((int pieceIndex) =>
                PieceReadResult.Success(pieceIndex, new byte[16384], true));

        mock.Setup(m => m.VerifyPiece(It.IsAny<int>(), It.IsAny<byte[]>()))
            .Returns(true);

        mock.Setup(m => m.HasValidPiece(It.IsAny<int>()))
            .Returns((int pieceIndex) => pieceIndex < completedPieces.Length && completedPieces[pieceIndex]);

        mock.Setup(m => m.IsPieceComplete(It.IsAny<int>()))
            .Returns((int pieceIndex) => pieceIndex < completedPieces.Length && completedPieces[pieceIndex]);

        mock.Setup(m => m.RaiseDiskFenceAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        mock.Setup(m => m.UpdateBasePath(It.IsAny<string>()));
        mock.Setup(m => m.LowerDiskFence());
        mock.Setup(m => m.ReleaseWriteHandlesAsync()).Returns(ValueTask.CompletedTask);

        return mock;
    }

    private static int CountTrue(BitArray array)
    {
        int count = 0;
        for (int i = 0; i < array.Length; i++)
        {
            if (array[i]) count++;
        }
        return count;
    }

    #endregion

    #region Statistics and Manager Mocks

    /// <summary>
    /// Creates a mock IStatisticsTracker.
    /// </summary>
    public static Mock<IStatisticsTracker> CreateStatisticsTrackerMock()
    {
        var mock = new Mock<IStatisticsTracker>();

        mock.Setup(m => m.TotalDownloaded).Returns(0);
        mock.Setup(m => m.TotalUploaded).Returns(0);
        mock.Setup(m => m.SessionDownloaded).Returns(0);
        mock.Setup(m => m.SessionUploaded).Returns(0);
        mock.Setup(m => m.DownloadRate).Returns(0);
        mock.Setup(m => m.UploadRate).Returns(0);
        mock.Setup(m => m.PayloadDownloaded).Returns(0);
        mock.Setup(m => m.PayloadUploaded).Returns(0);
        mock.Setup(m => m.PayloadDownloadRate).Returns(0);
        mock.Setup(m => m.PayloadUploadRate).Returns(0);
        mock.Setup(m => m.PiecesCompleted).Returns(0);
        mock.Setup(m => m.PiecesUploaded).Returns(0);
        mock.Setup(m => m.FailedBytes).Returns(0);
        mock.Setup(m => m.VerifiedDownloaded).Returns(0);
        mock.Setup(m => m.VerifiedDownloadRate).Returns(0);
        mock.Setup(m => m.SmoothedPayloadDownloadRate).Returns(0);
        mock.Setup(m => m.EndgameWastedBytes).Returns(0);
        mock.Setup(m => m.EndgameDuplicateBlocks).Returns(0);
        mock.Setup(m => m.TrackedPeerCount).Returns(0);
        mock.Setup(m => m.GetAllPeerStats()).Returns(new Dictionary<IPeerConnection, PeerTransferStats>());

        mock.Setup(m => m.GetPeerDownloadRate(It.IsAny<IPeerConnection>())).Returns(0);
        mock.Setup(m => m.GetPeerUploadRate(It.IsAny<IPeerConnection>())).Returns(0);
        mock.Setup(m => m.GetPeerDownloaded(It.IsAny<IPeerConnection>())).Returns(0);
        mock.Setup(m => m.GetPeerUploaded(It.IsAny<IPeerConnection>())).Returns(0);

        return mock;
    }

    /// <summary>
    /// Creates a mock IChokingManager.
    /// </summary>
    public static Mock<IChokingManager> CreateChokingManagerMock(bool defaultUnchoked = false)
    {
        var mock = new Mock<IChokingManager>();

        mock.Setup(m => m.IsPeerUnchoked(It.IsAny<IPeerConnection>()))
            .Returns(defaultUnchoked);
        mock.Setup(m => m.UnchokedPeerCount).Returns(0);
        mock.Setup(m => m.TotalUploaded).Returns(0);
        mock.Setup(m => m.TotalDownloaded).Returns(0);

        mock.Setup(m => m.HandlePeerInterestedAsync(It.IsAny<IPeerConnection>(), It.IsAny<PeerMessage>()))
            .Returns(Task.CompletedTask);
        mock.Setup(m => m.HandlePeerNotInterestedAsync(It.IsAny<IPeerConnection>(), It.IsAny<PeerMessage>()))
            .Returns(Task.CompletedTask);
        mock.Setup(m => m.StartAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(m => m.StopAsync())
            .Returns(Task.CompletedTask);

        return mock;
    }

    /// <summary>
    /// Creates a mock IEndgameStrategy.
    /// </summary>
    public static Mock<IEndgameStrategy> CreateEndgameStrategyMock()
    {
        var mock = new Mock<IEndgameStrategy>();

        mock.Setup(m => m.WastedBytes).Returns(0);
        mock.Setup(m => m.DuplicateBlockCount).Returns(0);

        mock.Setup(m => m.PickDuplicateBlock(
                It.IsAny<IPeerConnection>(),
                It.IsAny<IReadOnlyDictionary<int, PieceBlockTracker>>(),
                It.IsAny<ConcurrentDictionary<BlockRequest, PendingBlock>>(),
                It.IsAny<Func<IPeerConnection, int, bool>>()))
            .Returns((BlockRequest?)null);

        mock.Setup(m => m.PickDuplicateBlocks(
                It.IsAny<IPeerConnection>(),
                It.IsAny<IReadOnlyDictionary<int, PieceBlockTracker>>(),
                It.IsAny<ConcurrentDictionary<BlockRequest, PendingBlock>>(),
                It.IsAny<Func<IPeerConnection, int, bool>>(),
                It.IsAny<int>()))
            .Returns(new List<BlockRequest>());

        mock.Setup(m => m.OnBlockReceived(
                It.IsAny<BlockRequest>(),
                It.IsAny<IPeerConnection>()))
            .Returns(false);

        return mock;
    }

    #endregion

    #region Logger Mocks

    /// <summary>
    /// Creates a mock ILogger of specified type.
    /// </summary>
    public static Mock<ILogger<T>> CreateLoggerMock<T>()
    {
        return new Mock<ILogger<T>>();
    }

    /// <summary>
    /// Creates a mock ILoggerFactory.
    /// </summary>
    public static Mock<ILoggerFactory> CreateLoggerFactoryMock()
    {
        var mock = new Mock<ILoggerFactory>();
        mock.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);
        return mock;
    }

    #endregion

    #region Bitfield Helpers

    /// <summary>
    /// Creates a Bitfield with specified completed pieces.
    /// </summary>
    public static Bitfield CreateBitfield(int pieceCount, params int[] completedPieces)
    {
        var bitfield = new Bitfield(pieceCount);
        foreach (var piece in completedPieces)
        {
            if (piece >= 0 && piece < pieceCount)
            {
                bitfield.SetPiece(piece);
            }
        }
        return bitfield;
    }

    /// <summary>
    /// Creates a complete Bitfield.
    /// </summary>
    public static Bitfield CreateCompleteBitfield(int pieceCount)
    {
        var bitfield = new Bitfield(pieceCount);
        bitfield.SetAll();
        return bitfield;
    }

    #endregion
}
