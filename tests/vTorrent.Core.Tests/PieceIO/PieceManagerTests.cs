using System.Collections;
using FluentAssertions;
using Moq;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.PieceIO;
using vTorrent.Tests.Mocks;
using Xunit;

namespace vTorrent.Tests.Unit.PieceIO;

public class PieceManagerTests : IDisposable
{
    private readonly string _testBasePath;
    private readonly TorrentInfo _torrentInfo;
    private readonly Mock<IFileLockManager> _lockManagerMock;
    private PieceManager _pieceManager;

    private const int TestPieceCount = 10;
    private const int TestPieceLength = 16384;

    public PieceManagerTests()
    {
        // Create a temporary directory for tests
        _testBasePath = Path.Combine(Path.GetTempPath(), "vTorrentTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testBasePath);

        _torrentInfo = MockFactories.CreateTorrentInfo(TestPieceCount, TestPieceLength);
        _lockManagerMock = new Mock<IFileLockManager>();

        // Setup lock manager to return a disposable that does nothing
        _lockManagerMock.Setup(m => m.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DisposableStub());
        _lockManagerMock.Setup(m => m.AcquireLock(It.IsAny<string>()))
            .Returns(new DisposableStub());

        // Create piece manager with skip verification to avoid file system issues
        _pieceManager = new PieceManager(
            _testBasePath,
            _torrentInfo,
            _lockManagerMock.Object,
            new vTorrent.Core.Session.TorrentStatistics(),
            skipInitialVerification: true);
    }

    public void Dispose()
    {
        _pieceManager?.Dispose();

        // Clean up test directory
        try
        {
            if (Directory.Exists(_testBasePath))
            {
                Directory.Delete(_testBasePath, true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullBasePath_ShouldThrow()
    {
        var act = () => new PieceManager(
            null!,
            _torrentInfo,
            _lockManagerMock.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("basePath");
    }

    [Fact]
    public void Constructor_WithNullTorrentInfo_ShouldThrow()
    {
        var act = () => new PieceManager(
            _testBasePath,
            null!,
            _lockManagerMock.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("torrentInfo");
    }

    [Fact]
    public void Constructor_WithNullLockManager_ShouldThrow()
    {
        var act = () => new PieceManager(
            _testBasePath,
            _torrentInfo,
            null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("lockManager");
    }

    [Fact]
    public void Constructor_ShouldInitializeCorrectly()
    {
        _pieceManager.TotalPieces.Should().Be(TestPieceCount);
        _pieceManager.PieceSize.Should().Be(TestPieceLength);
        _pieceManager.CompletedPieceCount.Should().Be(0);
        _pieceManager.IsFenced.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithSkipVerification_ShouldNotVerifyPieces()
    {
        // Piece manager was created with skipInitialVerification: true
        // Should not attempt to read any files during construction
        _pieceManager.CompletedPieceCount.Should().Be(0);
    }

    #endregion

    #region Property Tests

    [Fact]
    public void TotalPieces_ShouldMatchTorrentInfo()
    {
        _pieceManager.TotalPieces.Should().Be(TestPieceCount);
    }

    [Fact]
    public void PieceSize_ShouldMatchTorrentInfo()
    {
        _pieceManager.PieceSize.Should().Be(TestPieceLength);
    }

    [Fact]
    public void DiskStats_ShouldNotBeNull()
    {
        _pieceManager.DiskStats.Should().NotBeNull();
    }

    [Fact]
    public void IsFenced_InitialValue_ShouldBeFalse()
    {
        _pieceManager.IsFenced.Should().BeFalse();
    }

    #endregion

    #region GetBitfield Tests

    [Fact]
    public void GetBitfield_ShouldReturnCorrectLength()
    {
        var bitfield = _pieceManager.GetBitfield();

        bitfield.Should().NotBeNull();
        bitfield.Length.Should().Be(TestPieceCount);
    }

    [Fact]
    public void GetBitfield_InitialState_ShouldBeAllFalse()
    {
        var bitfield = _pieceManager.GetBitfield();

        for (int i = 0; i < bitfield.Length; i++)
        {
            bitfield[i].Should().BeFalse();
        }
    }

    #endregion

    #region SetPieceComplete Tests

    [Fact]
    public void SetPieceComplete_ShouldMarkPieceComplete()
    {
        _pieceManager.SetPieceComplete(0, true);

        _pieceManager.IsPieceComplete(0).Should().BeTrue();
        _pieceManager.CompletedPieceCount.Should().Be(1);
    }

    [Fact]
    public void SetPieceComplete_WithFalse_ShouldMarkPieceIncomplete()
    {
        _pieceManager.SetPieceComplete(0, true);
        _pieceManager.SetPieceComplete(0, false);

        _pieceManager.IsPieceComplete(0).Should().BeFalse();
        _pieceManager.CompletedPieceCount.Should().Be(0);
    }

    [Fact]
    public void SetPieceComplete_MultiplePieces_ShouldTrackCorrectly()
    {
        _pieceManager.SetPieceComplete(0, true);
        _pieceManager.SetPieceComplete(5, true);
        _pieceManager.SetPieceComplete(9, true);

        _pieceManager.CompletedPieceCount.Should().Be(3);
        _pieceManager.IsPieceComplete(0).Should().BeTrue();
        _pieceManager.IsPieceComplete(5).Should().BeTrue();
        _pieceManager.IsPieceComplete(9).Should().BeTrue();
        _pieceManager.IsPieceComplete(1).Should().BeFalse();
    }

    #endregion

    #region IsPieceComplete Tests

    [Fact]
    public void IsPieceComplete_WithIncompletePiece_ShouldReturnFalse()
    {
        _pieceManager.IsPieceComplete(0).Should().BeFalse();
    }

    [Fact]
    public void IsPieceComplete_WithCompletePiece_ShouldReturnTrue()
    {
        _pieceManager.SetPieceComplete(0, true);

        _pieceManager.IsPieceComplete(0).Should().BeTrue();
    }

    #endregion

    #region InitializeFromResumeBitfield Tests

    [Fact]
    public void InitializeFromResumeBitfield_ShouldSetBitfield()
    {
        var resumeBitfield = new BitArray(TestPieceCount);
        resumeBitfield[0] = true;
        resumeBitfield[3] = true;
        resumeBitfield[7] = true;

        _pieceManager.InitializeFromResumeBitfield(resumeBitfield);

        _pieceManager.IsPieceComplete(0).Should().BeTrue();
        _pieceManager.IsPieceComplete(3).Should().BeTrue();
        _pieceManager.IsPieceComplete(7).Should().BeTrue();
        _pieceManager.IsPieceComplete(1).Should().BeFalse();
        _pieceManager.CompletedPieceCount.Should().Be(3);
    }

    [Fact]
    public void InitializeFromResumeBitfield_WithAllComplete_ShouldSetAllBits()
    {
        var resumeBitfield = new BitArray(TestPieceCount, true);

        _pieceManager.InitializeFromResumeBitfield(resumeBitfield);

        _pieceManager.CompletedPieceCount.Should().Be(TestPieceCount);
    }

    [Fact]
    public void InitializeFromResumeBitfield_WithNone_ShouldNotSetAnyBits()
    {
        var resumeBitfield = new BitArray(TestPieceCount, false);

        _pieceManager.InitializeFromResumeBitfield(resumeBitfield);

        _pieceManager.CompletedPieceCount.Should().Be(0);
    }

    #endregion

    #region HasValidPiece Tests

    [Fact]
    public void HasValidPiece_WithIncompletePiece_ShouldReturnFalse()
    {
        _pieceManager.HasValidPiece(0).Should().BeFalse();
    }

    [Fact]
    public void HasValidPiece_WithCompletePiece_ShouldReturnTrue()
    {
        _pieceManager.SetPieceComplete(0, true);

        _pieceManager.HasValidPiece(0).Should().BeTrue();
    }

    #endregion

    #region RaiseDiskFenceAsync Tests

    [Fact]
    public async Task RaiseDiskFenceAsync_ShouldSetFenced()
    {
        var result = await _pieceManager.RaiseDiskFenceAsync(TimeSpan.FromSeconds(5));

        result.Should().BeTrue();
        _pieceManager.IsFenced.Should().BeTrue();
    }

    [Fact]
    public async Task RaiseDiskFenceAsync_MultipleCalls_ShouldSucceed()
    {
        await _pieceManager.RaiseDiskFenceAsync(TimeSpan.FromSeconds(5));
        var result = await _pieceManager.RaiseDiskFenceAsync(TimeSpan.FromSeconds(5));

        result.Should().BeTrue();
        _pieceManager.IsFenced.Should().BeTrue();
    }

    #endregion

    #region LowerDiskFence Tests

    [Fact]
    public async Task LowerDiskFence_AfterRaise_ShouldClearFenced()
    {
        await _pieceManager.RaiseDiskFenceAsync(TimeSpan.FromSeconds(5));

        _pieceManager.LowerDiskFence();

        _pieceManager.IsFenced.Should().BeFalse();
    }

    [Fact]
    public void LowerDiskFence_WithoutRaise_ShouldNotThrow()
    {
        var act = () => _pieceManager.LowerDiskFence();

        act.Should().NotThrow();
    }

    #endregion

    #region UpdateBasePath Tests

    [Fact]
    public async Task UpdateBasePath_AfterFence_ShouldUpdatePath()
    {
        await _pieceManager.RaiseDiskFenceAsync(TimeSpan.FromSeconds(5));
        var newPath = Path.Combine(Path.GetTempPath(), "vTorrentTests", Guid.NewGuid().ToString());

        var act = () => _pieceManager.UpdateBasePath(newPath);

        act.Should().NotThrow();
    }

    #endregion

    #region ReleaseWriteHandles Tests

    [Fact]
    public async Task ReleaseWriteHandlesAsync_ShouldNotThrow()
    {
        var act = async () => await _pieceManager.ReleaseWriteHandlesAsync();

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        var pieceManager = new PieceManager(
            _testBasePath,
            _torrentInfo,
            _lockManagerMock.Object,
            new vTorrent.Core.Session.TorrentStatistics(),
            skipInitialVerification: true);

        var act = () => pieceManager.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_MultipleCalls_ShouldNotThrow()
    {
        var pieceManager = new PieceManager(
            _testBasePath,
            _torrentInfo,
            _lockManagerMock.Object,
            new vTorrent.Core.Session.TorrentStatistics(),
            skipInitialVerification: true);

        pieceManager.Dispose();
        var act = () => pieceManager.Dispose();

        act.Should().NotThrow();
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public void ConcurrentSetPieceComplete_ShouldNotThrow()
    {
        var tasks = new List<Task>();

        for (int i = 0; i < TestPieceCount; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() => _pieceManager.SetPieceComplete(index, true)));
        }

        var act = () => Task.WaitAll(tasks.ToArray());

        act.Should().NotThrow();
        _pieceManager.CompletedPieceCount.Should().Be(TestPieceCount);
    }

    [Fact]
    public void ConcurrentGetBitfield_ShouldNotThrow()
    {
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    _ = _pieceManager.GetBitfield();
                    _ = _pieceManager.CompletedPieceCount;
                }
            }));
        }

        var act = () => Task.WaitAll(tasks.ToArray());

        act.Should().NotThrow();
    }

    [Fact]
    public void ConcurrentReadAndWrite_ShouldNotThrow()
    {
        var tasks = new List<Task>();

        // Writers
        for (int i = 0; i < 5; i++)
        {
            var startIndex = i * 2;
            tasks.Add(Task.Run(() =>
            {
                _pieceManager.SetPieceComplete(startIndex, true);
                _pieceManager.SetPieceComplete(startIndex + 1, true);
            }));
        }

        // Readers
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    _ = _pieceManager.CompletedPieceCount;
                    _ = _pieceManager.IsPieceComplete(0);
                    _ = _pieceManager.GetBitfield();
                }
            }));
        }

        var act = () => Task.WaitAll(tasks.ToArray());

        act.Should().NotThrow();
    }

    #endregion

    #region CompletedPieceCount Tests

    [Fact]
    public void CompletedPieceCount_Initially_ShouldBeZero()
    {
        _pieceManager.CompletedPieceCount.Should().Be(0);
    }

    [Fact]
    public void CompletedPieceCount_AfterSetPieceComplete_ShouldIncrement()
    {
        _pieceManager.SetPieceComplete(0, true);
        _pieceManager.SetPieceComplete(1, true);
        _pieceManager.SetPieceComplete(2, true);

        _pieceManager.CompletedPieceCount.Should().Be(3);
    }

    [Fact]
    public void CompletedPieceCount_AfterClearPiece_ShouldDecrement()
    {
        _pieceManager.SetPieceComplete(0, true);
        _pieceManager.SetPieceComplete(1, true);
        _pieceManager.SetPieceComplete(0, false);

        _pieceManager.CompletedPieceCount.Should().Be(1);
    }

    #endregion

    #region Helper Classes

    private class DisposableStub : IDisposable
    {
        public void Dispose() { }
    }

    #endregion
}
