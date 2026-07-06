using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using vTorrent.Core.PieceIO;
using Xunit;

namespace vTorrent.Tests.Unit.PieceIO;

public class PartFileTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILogger _logger;

    private const int NumPieces = 10;
    private const int PieceSize = 16384; // 16 KiB
    private const string FileName = "test.parts";

    public PartFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PartFileTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _logger = new Mock<ILogger>().Object;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // ignore cleanup errors
        }
    }

    private PartFile CreatePartFile(int numPieces = NumPieces, int pieceSize = PieceSize)
        => new PartFile(_tempDir, FileName, numPieces, pieceSize, _logger);

    private static byte[] MakeData(int length, byte fill = 0xAB)
    {
        var data = new byte[length];
        Array.Fill(data, fill);
        return data;
    }

    // -----------------------------------------------------------------------
    // Test 1: Constructor creates no file until first write (lazy)
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_DoesNotCreateFile_WhenNoDataWritten()
    {
        using var pf = CreatePartFile();

        var path = Path.Combine(_tempDir, FileName);
        File.Exists(path).Should().BeFalse("partfile should be lazy — no file until first write");
    }

    // -----------------------------------------------------------------------
    // Test 2: Write then Read round-trips correctly
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteAsync_ThenReadAsync_RoundTripsData()
    {
        var written = MakeData(PieceSize, 0x42);

        using var pf = CreatePartFile();
        await pf.WriteAsync(written.AsMemory(), pieceIndex: 0, offset: 0);

        var readBuf = new byte[PieceSize];
        var bytesRead = await pf.ReadAsync(readBuf.AsMemory(), pieceIndex: 0, offset: 0);

        bytesRead.Should().Be(PieceSize);
        readBuf.Should().Equal(written);
    }

    // -----------------------------------------------------------------------
    // Test 3: Read returns 0 when piece not stored
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_ReturnsZero_WhenPieceNotStored()
    {
        using var pf = CreatePartFile();
        var buf = new byte[PieceSize];
        var result = await pf.ReadAsync(buf.AsMemory(), pieceIndex: 3, offset: 0);

        result.Should().Be(0);
    }

    // -----------------------------------------------------------------------
    // Test 4: HasPiece returns true after write
    // -----------------------------------------------------------------------

    [Fact]
    public async Task HasPiece_ReturnsTrue_AfterWrite()
    {
        using var pf = CreatePartFile();
        pf.HasPiece(5).Should().BeFalse();

        await pf.WriteAsync(MakeData(PieceSize).AsMemory(), pieceIndex: 5, offset: 0);

        pf.HasPiece(5).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Test 5: FreePiece removes slot, HasPiece returns false
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FreePiece_RemovesSlot_HasPieceReturnsFalse()
    {
        using var pf = CreatePartFile();
        await pf.WriteAsync(MakeData(PieceSize).AsMemory(), pieceIndex: 2, offset: 0);

        pf.HasPiece(2).Should().BeTrue();
        pf.FreePiece(2);
        pf.HasPiece(2).Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Test 6: Write at non-zero offset within piece slot
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteAsync_AtNonZeroOffset_ReadsBackCorrectly()
    {
        const int writeOffset = 512;
        const int writeLen = 1024;

        using var pf = CreatePartFile();

        // First write to allocate the slot (full piece area so slot is valid)
        var full = new byte[PieceSize];
        await pf.WriteAsync(full.AsMemory(), pieceIndex: 1, offset: 0);

        // Overwrite a sub-range
        var patch = MakeData(writeLen, 0xFF);
        await pf.WriteAsync(patch.AsMemory(), pieceIndex: 1, offset: writeOffset);

        // Read back and verify the patched region
        var readBuf = new byte[writeLen];
        var bytesRead = await pf.ReadAsync(readBuf.AsMemory(), pieceIndex: 1, offset: writeOffset);

        bytesRead.Should().Be(writeLen);
        readBuf.Should().Equal(patch);
    }

    // -----------------------------------------------------------------------
    // Test 7: Freed slots are reused by new writes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FreedSlots_AreReused_ByNewWrites()
    {
        using var pf = CreatePartFile();

        // Write pieces 0 and 1 — they take slots 0 and 1
        await pf.WriteAsync(MakeData(PieceSize, 0x01).AsMemory(), pieceIndex: 0, offset: 0);
        await pf.WriteAsync(MakeData(PieceSize, 0x02).AsMemory(), pieceIndex: 1, offset: 0);

        // Free slot for piece 0
        pf.FreePiece(0);

        // Write piece 2 — should reuse the freed slot
        var data2 = MakeData(PieceSize, 0x03);
        await pf.WriteAsync(data2.AsMemory(), pieceIndex: 2, offset: 0);

        // Read back piece 2 to confirm data is correct
        var readBuf = new byte[PieceSize];
        var bytesRead = await pf.ReadAsync(readBuf.AsMemory(), pieceIndex: 2, offset: 0);
        bytesRead.Should().Be(PieceSize);
        readBuf.Should().Equal(data2);

        // Piece 1 should still be intact
        var buf1 = new byte[PieceSize];
        await pf.ReadAsync(buf1.AsMemory(), pieceIndex: 1, offset: 0);
        buf1.Should().Equal(MakeData(PieceSize, 0x02));
    }

    // -----------------------------------------------------------------------
    // Test 8: Persistence — data survives Dispose + reopen
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Data_SurvivesDisposeAndReopen()
    {
        var written = MakeData(PieceSize, 0x77);

        // Write and dispose
        var pf1 = CreatePartFile();
        await pf1.WriteAsync(written.AsMemory(), pieceIndex: 3, offset: 0);
        pf1.Dispose();

        // Reopen
        using var pf2 = CreatePartFile();
        pf2.HasPiece(3).Should().BeTrue();

        var readBuf = new byte[PieceSize];
        var bytesRead = await pf2.ReadAsync(readBuf.AsMemory(), pieceIndex: 3, offset: 0);
        bytesRead.Should().Be(PieceSize);
        readBuf.Should().Equal(written);
    }

    // -----------------------------------------------------------------------
    // Test 9: Corrupted header treated as empty (no exception)
    // -----------------------------------------------------------------------

    [Fact]
    public void CorruptedHeader_TreatedAsEmpty_NoException()
    {
        var path = Path.Combine(_tempDir, FileName);
        // Write garbage bytes — truncated header
        File.WriteAllBytes(path, new byte[] { 0xDE, 0xAD, 0xBE }); // 3 bytes, not a valid header

        PartFile? pf = null;
        var act = () => { pf = CreatePartFile(); };
        act.Should().NotThrow();
        pf.Should().NotBeNull();
        pf!.HasPiece(0).Should().BeFalse("corrupted header should result in empty state");
        pf.Dispose();
    }

    // -----------------------------------------------------------------------
    // Test 10: Mismatched metadata treated as empty
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MismatchedMetadata_TreatedAsEmpty()
    {
        // Write with 10 pieces
        var pf1 = CreatePartFile(numPieces: 10);
        await pf1.WriteAsync(MakeData(PieceSize).AsMemory(), pieceIndex: 0, offset: 0);
        pf1.Dispose();

        // Reopen with different numPieces — should treat as empty
        using var pf2 = new PartFile(_tempDir, FileName, numPieces: 5, pieceSize: PieceSize, _logger);
        pf2.HasPiece(0).Should().BeFalse("mismatched numPieces should result in empty state");
    }

    // -----------------------------------------------------------------------
    // Test 11: Dispose deletes file when empty (all pieces freed)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Dispose_DeletesFile_WhenEmpty()
    {
        var pf = CreatePartFile();
        await pf.WriteAsync(MakeData(PieceSize).AsMemory(), pieceIndex: 0, offset: 0);
        pf.FreePiece(0);
        pf.Dispose();

        var path = Path.Combine(_tempDir, FileName);
        File.Exists(path).Should().BeFalse("file should be deleted when all slots are freed");
    }

    // -----------------------------------------------------------------------
    // Test 12: Dispose keeps file when not empty
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Dispose_KeepsFile_WhenNotEmpty()
    {
        var pf = CreatePartFile();
        await pf.WriteAsync(MakeData(PieceSize).AsMemory(), pieceIndex: 0, offset: 0);
        pf.Dispose();

        var path = Path.Combine(_tempDir, FileName);
        File.Exists(path).Should().BeTrue("file should remain when pieces are still stored");
    }

    // -----------------------------------------------------------------------
    // Test 13: ExportFileAsync exports correct data and frees slot when fully exportable
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExportFileAsync_ExportsData_AndFreesSlot_WhenFullyExportable()
    {
        var pieceData = MakeData(PieceSize, 0xCC);

        using var pf = CreatePartFile();
        await pf.WriteAsync(pieceData.AsMemory(), pieceIndex: 0, offset: 0);

        byte[]? captured = null;
        long capturedOffset = -1;

        async ValueTask Callback(long fileOffset, ReadOnlyMemory<byte> data)
        {
            capturedOffset = fileOffset;
            captured = data.ToArray();
            await Task.CompletedTask;
        }

        // Export: file covers exactly piece 0
        await pf.ExportFileAsync(
            writeCallback: Callback,
            fileOffset: 0,
            fileSize: PieceSize,
            isPieceFullyExportable: _ => true);

        captured.Should().NotBeNull();
        captured.Should().Equal(pieceData);
        capturedOffset.Should().Be(0);

        // Slot should have been freed
        pf.HasPiece(0).Should().BeFalse("slot should be freed when isPieceFullyExportable returns true");
    }

    // -----------------------------------------------------------------------
    // Test 14: ExportFileAsync retains slot when isPieceFullyExportable=false
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExportFileAsync_RetainsSlot_WhenNotFullyExportable()
    {
        var pieceData = MakeData(PieceSize, 0xDD);

        using var pf = CreatePartFile();
        await pf.WriteAsync(pieceData.AsMemory(), pieceIndex: 0, offset: 0);

        async ValueTask Callback(long fileOffset, ReadOnlyMemory<byte> data)
        {
            await Task.CompletedTask;
        }

        await pf.ExportFileAsync(
            writeCallback: Callback,
            fileOffset: 0,
            fileSize: PieceSize,
            isPieceFullyExportable: _ => false);

        // Slot should still be present
        pf.HasPiece(0).Should().BeTrue("slot should be retained when isPieceFullyExportable returns false");
    }
}
