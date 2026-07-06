using vTorrent.Core.Download;
using Xunit;

namespace vTorrent.Core.Tests.Download;

public class PieceExtentAffinityTests
{
    [Theory]
    [InlineData(256 * 1024, 4 * 1024 * 1024, 5, 0)]   // 256KB pieces, 4MB extent → piece 5 in extent 0
    [InlineData(256 * 1024, 4 * 1024 * 1024, 20, 16)]  // piece 20 in extent starting at 16
    [InlineData(1024 * 1024, 4 * 1024 * 1024, 3, 0)]   // 1MB pieces → piece 3 in extent 0
    [InlineData(1024 * 1024, 4 * 1024 * 1024, 5, 4)]   // piece 5 in extent starting at 4
    public void ExtentGroup_CalculatesCorrectStart(int pieceLength, int extentSize, int pieceIndex, int expectedExtentStart)
    {
        int extentPieceCount = Math.Max(1, extentSize / pieceLength);
        int extentStart = (pieceIndex / extentPieceCount) * extentPieceCount;
        Assert.Equal(expectedExtentStart, extentStart);
    }

    [Fact]
    public void ExtentPieceCount_CeilingForNonDivisible()
    {
        int extentPieceCount = (int)Math.Ceiling(1_048_576.0 / 307_200);
        Assert.Equal(4, extentPieceCount);
    }

    [Fact]
    public void PickPiece_WithExtentAffinity_PrefersSameExtent()
    {
        // Create a picker with 32 pieces
        var picker = new BucketPiecePicker(32);

        // Give all pieces availability of 1
        for (int i = 0; i < 32; i++)
            picker.IncrementAvailability(i);

        // Set extent: pieceLength=256KB, extentSize=4MB → 16 pieces per extent
        // Extent 0: pieces 0-15, Extent 1: pieces 16-31

        // Pick first piece — should come from rarest-first (any piece)
        int? first = picker.PickPiece(i => true, extentPieceLength: 256 * 1024, extentSize: 4 * 1024 * 1024);
        Assert.NotNull(first);

        // Mark first piece as in-progress
        picker.MarkInProgress(first!.Value);

        // Pick second piece with extent affinity — should prefer same extent
        int? second = picker.PickPiece(i => true, extentPieceLength: 256 * 1024, extentSize: 4 * 1024 * 1024);
        Assert.NotNull(second);

        // Both should be in the same extent
        int extentPieceCount = 4 * 1024 * 1024 / (256 * 1024); // 16
        int firstExtent = first!.Value / extentPieceCount;
        int secondExtent = second!.Value / extentPieceCount;
        Assert.Equal(firstExtent, secondExtent);
    }

    [Fact]
    public void PickPiece_WithoutExtentAffinity_NormalRarestFirst()
    {
        var picker = new BucketPiecePicker(32);
        for (int i = 0; i < 32; i++)
            picker.IncrementAvailability(i);

        // Without extent params, should use normal rarest-first
        int? piece = picker.PickPiece(i => true);
        Assert.NotNull(piece);
    }

    [Fact]
    public void ExtentLargerThanTorrent_SingleExtent()
    {
        // 10 pieces total, 4MB extent with 256KB pieces (16 per extent) → all in extent 0
        var picker = new BucketPiecePicker(10);
        for (int i = 0; i < 10; i++)
            picker.IncrementAvailability(i);

        int? piece = picker.PickPiece(i => true, extentPieceLength: 256 * 1024, extentSize: 4 * 1024 * 1024);
        Assert.NotNull(piece);

        // All pieces are in extent 0, so the result is valid regardless
        int extentPieceCount = 4 * 1024 * 1024 / (256 * 1024);
        Assert.Equal(0, piece!.Value / extentPieceCount);
    }
}
