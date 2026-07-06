using FluentAssertions;
using vTorrent.Core.ResumeData;
using Xunit;

namespace vTorrent.Core.Tests.ResumeData;

public class NoVerifyFlagsTests
{
    [Fact]
    public void DefaultFlags_DoesNotIncludeNoVerifyFiles()
    {
        TorrentFlags.DefaultFlags.HasFlag(TorrentFlags.NoVerifyFiles).Should().BeFalse();
    }

    [Fact]
    public void NoVerifyFiles_CanBeSetAndPersisted()
    {
        var resume = new TorrentResumeData
        {
            InfoHash = "AABBCCDD00112233AABBCCDD00112233AABBCCDD",
            Name = "test-torrent",
            PieceCount = 100,
            PieceLength = 262144,
        };

        resume.Flags.HasFlag(TorrentFlags.NoVerifyFiles).Should().BeFalse();
        resume.Flags |= TorrentFlags.NoVerifyFiles;

        var serialized = ResumeDataSerializer.Serialize(resume);
        var deserialized = ResumeDataSerializer.Deserialize(serialized);

        deserialized.Flags.HasFlag(TorrentFlags.NoVerifyFiles).Should().BeTrue();
    }

    [Fact]
    public void NoVerifyFiles_ClearedOnCrashRecovery()
    {
        var resume = new TorrentResumeData
        {
            InfoHash = "AABBCCDD00112233AABBCCDD00112233AABBCCDD",
            Name = "test-torrent",
            PieceCount = 100,
            PieceLength = 262144,
            NeedsCrashRecovery = true,
        };

        resume.Flags |= TorrentFlags.NoVerifyFiles;
        resume.Flags &= ~TorrentFlags.NoVerifyFiles;
        resume.Flags.HasFlag(TorrentFlags.NoVerifyFiles).Should().BeFalse();
    }
}
