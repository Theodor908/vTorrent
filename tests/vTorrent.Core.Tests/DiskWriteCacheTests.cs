using FluentAssertions;
using Xunit;
using vTorrent.Core;
using vTorrent.Core.Download;

namespace vTorrent.Tests.Unit.Core;

public class DiskWriteCacheTests
{
    private const int BlockSize = 16384;

    [Fact]
    public void AddBlock_StoresBlockData()
    {
        var cache = new DiskWriteCache(memoryCap: 1024 * 1024);
        var blockData = new byte[BlockSize];
        Array.Fill(blockData, (byte)0xAB);

        bool added = cache.AddBlock(0, 65536, 0, blockData, BlockSize);
        added.Should().BeTrue();

        var pieceData = cache.GetPieceData(0);
        pieceData.Should().NotBeNull();
        pieceData![0].Should().Be(0xAB);
    }

    [Fact]
    public void AddBlock_MultipleBlocks_AssemblesPiece()
    {
        var cache = new DiskWriteCache(memoryCap: 1024 * 1024);
        int pieceSize = BlockSize * 4; // 64KB

        for (int i = 0; i < 4; i++)
        {
            var data = new byte[BlockSize];
            Array.Fill(data, (byte)(i + 1));
            cache.AddBlock(0, pieceSize, i * BlockSize, data, BlockSize);
        }

        var pieceData = cache.GetPieceData(0);
        pieceData.Should().NotBeNull();
        pieceData![0].Should().Be(1, "first block");
        pieceData[BlockSize].Should().Be(2, "second block");
        pieceData[BlockSize * 2].Should().Be(3, "third block");
        pieceData[BlockSize * 3].Should().Be(4, "fourth block");
    }

    [Fact]
    public void ReleasePiece_ReturnsBufferAndRemovesEntry()
    {
        var cache = new DiskWriteCache(memoryCap: 1024 * 1024);
        cache.AddBlock(0, 65536, 0, new byte[BlockSize], BlockSize);

        cache.ReleasePiece(0);

        cache.GetPieceData(0).Should().BeNull("piece was released");
        cache.TotalCachedBytes.Should().Be(0);
    }

    [Fact]
    public void DiscardPiece_SameAsRelease()
    {
        var cache = new DiskWriteCache(memoryCap: 1024 * 1024);
        cache.AddBlock(0, 65536, 0, new byte[BlockSize], BlockSize);

        cache.DiscardPiece(0);

        cache.GetPieceData(0).Should().BeNull();
        cache.TotalCachedBytes.Should().Be(0);
    }

    [Fact]
    public void MemoryCap_EvictsLRU()
    {
        // Cache cap = 2 pieces worth
        int pieceSize = BlockSize * 2; // 32KB per piece
        var cache = new DiskWriteCache(memoryCap: pieceSize * 2 + 100);

        // Add piece 0 and unprotect (simulates completed piece ready for eviction)
        cache.AddBlock(0, pieceSize, 0, new byte[BlockSize], BlockSize);
        cache.AddBlock(0, pieceSize, BlockSize, new byte[BlockSize], BlockSize);
        cache.UnprotectPiece(0);

        // Add piece 1 and unprotect
        cache.AddBlock(1, pieceSize, 0, new byte[BlockSize], BlockSize);
        cache.AddBlock(1, pieceSize, BlockSize, new byte[BlockSize], BlockSize);
        cache.UnprotectPiece(1);

        // Access piece 1 (make it recently used)
        cache.GetPieceData(1);

        // Add piece 2 — should evict piece 0 (LRU, unprotected)
        cache.AddBlock(2, pieceSize, 0, new byte[BlockSize], BlockSize);
        cache.AddBlock(2, pieceSize, BlockSize, new byte[BlockSize], BlockSize);

        cache.GetPieceData(0).Should().BeNull("piece 0 evicted as LRU");
        cache.GetPieceData(1).Should().NotBeNull("piece 1 recently accessed");
        cache.GetPieceData(2).Should().NotBeNull("piece 2 just added");
    }

    [Fact]
    public void MemoryCap_DoesNotEvictProtectedPieces()
    {
        int pieceSize = BlockSize * 2;
        var cache = new DiskWriteCache(memoryCap: pieceSize * 2 + 100);

        // Add piece 0 — auto-protected on creation (in-progress piece)
        cache.AddBlock(0, pieceSize, 0, new byte[BlockSize], BlockSize);
        cache.AddBlock(0, pieceSize, BlockSize, new byte[BlockSize], BlockSize);
        // Piece 0 stays protected (simulates still in-progress)

        // Add piece 1 and unprotect (simulates completed piece)
        cache.AddBlock(1, pieceSize, 0, new byte[BlockSize], BlockSize);
        cache.AddBlock(1, pieceSize, BlockSize, new byte[BlockSize], BlockSize);
        cache.UnprotectPiece(1);

        // Add piece 2 — should evict piece 1 (unprotected), NOT piece 0 (protected/in-progress)
        cache.AddBlock(2, pieceSize, 0, new byte[BlockSize], BlockSize);
        cache.AddBlock(2, pieceSize, BlockSize, new byte[BlockSize], BlockSize);

        cache.GetPieceData(0).Should().NotBeNull("in-progress piece protected from eviction");
        cache.GetPieceData(1).Should().BeNull("completed/unprotected piece evicted");
    }

    [Fact]
    public void TotalCachedBytes_TracksAccurately()
    {
        var cache = new DiskWriteCache(memoryCap: 1024 * 1024);

        cache.AddBlock(0, 65536, 0, new byte[BlockSize], BlockSize);
        cache.TotalCachedBytes.Should().Be(65536, "piece buffer allocated on first block");

        cache.ReleasePiece(0);
        cache.TotalCachedBytes.Should().Be(0);
    }

    [Fact]
    public void HasPieceData_ReturnsTrueWhenCached()
    {
        var cache = new DiskWriteCache(memoryCap: 1024 * 1024);

        cache.HasPieceData(0).Should().BeFalse();

        cache.AddBlock(0, 65536, 0, new byte[BlockSize], BlockSize);
        cache.HasPieceData(0).Should().BeTrue();
    }

    [Fact]
    public void DisposeAll_ClearsEverything()
    {
        var cache = new DiskWriteCache(memoryCap: 1024 * 1024);
        cache.AddBlock(0, 65536, 0, new byte[BlockSize], BlockSize);
        cache.AddBlock(1, 65536, 0, new byte[BlockSize], BlockSize);

        cache.DisposeAll();

        cache.TotalCachedBytes.Should().Be(0);
        cache.GetPieceData(0).Should().BeNull();
        cache.GetPieceData(1).Should().BeNull();
    }
}
