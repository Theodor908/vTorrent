using System;
using System.Collections;
using FluentAssertions;
using vTorrent.Core.ResumeData;
using Xunit;

namespace vTorrent.Core.Tests.Engine;

public class SeedModeStartupTests
{
    [Fact]
    public void SeedMode_HavePieces_AllOnes_ForPieceCount()
    {
        int pieceCount = 100;
        var resume = new TorrentResumeData
        {
            InfoHash = "AABBCCDD00112233AABBCCDD00112233AABBCCDD",
            Name = "seed-mode-test",
            PieceCount = pieceCount,
            PieceLength = 262144,
        };

        resume.Flags |= TorrentFlags.SeedMode;
        var allOnes = new BitArray(pieceCount, true);
        resume.SetHavePieces(allOnes);
        resume.VerifiedPieces = new byte[(pieceCount + 7) / 8];

        resume.SeedMode.Should().BeTrue();
        resume.HavePieces.Should().NotBeNull();
        resume.HavePieces!.Length.Should().Be((pieceCount + 7) / 8);
        resume.VerifiedPieces!.Length.Should().Be((pieceCount + 7) / 8);

        var haveBits = TorrentResumeData.BytesToBitArrayMsbFirst(resume.HavePieces, pieceCount);
        for (int i = 0; i < pieceCount; i++)
            haveBits[i].Should().BeTrue($"piece {i} should be marked as have");

        var verifiedBits = TorrentResumeData.BytesToBitArrayMsbFirst(resume.VerifiedPieces, pieceCount);
        for (int i = 0; i < pieceCount; i++)
            verifiedBits[i].Should().BeFalse($"piece {i} should not be verified yet");
    }

    [Fact]
    public void SeedMode_Flag_SurvivesSerializationRoundTrip()
    {
        int pieceCount = 50;
        var resume = new TorrentResumeData
        {
            InfoHash = "AABBCCDD00112233AABBCCDD00112233AABBCCDD",
            Name = "seed-mode-persist",
            PieceCount = pieceCount,
            PieceLength = 262144,
        };

        resume.Flags |= TorrentFlags.SeedMode;
        var allOnes = new BitArray(pieceCount, true);
        resume.SetHavePieces(allOnes);
        resume.VerifiedPieces = new byte[(pieceCount + 7) / 8];

        var serialized = ResumeDataSerializer.Serialize(resume);
        var loaded = ResumeDataSerializer.Deserialize(serialized);

        loaded.SeedMode.Should().BeTrue();
        loaded.HavePieces.Should().NotBeNull();
        loaded.VerifiedPieces.Should().NotBeNull();

        var haveBits = TorrentResumeData.BytesToBitArrayMsbFirst(loaded.HavePieces!, pieceCount);
        for (int i = 0; i < pieceCount; i++)
            haveBits[i].Should().BeTrue();
    }
}
