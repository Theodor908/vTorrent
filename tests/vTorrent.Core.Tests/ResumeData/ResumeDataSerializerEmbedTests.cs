using FluentAssertions;
using vTorrent.Core.ResumeData;
using Xunit;

namespace vTorrent.Core.Tests.ResumeData;

public class ResumeDataSerializerEmbedTests
{
    [Fact]
    public void RoundTrip_TorrentFileBytes_PreservedExactly()
    {
        var originalBytes = new byte[] { 0x64, 0x38, 0x3A, 0x61, 0x6E, 0x6E, 0x6F, 0x75, 0x6E, 0x63, 0x65, 0x65 };
        var resume = new TorrentResumeData
        {
            InfoHash = "AABBCCDD00112233AABBCCDD00112233AABBCCDD",
            Name = "test-torrent",
            PieceCount = 100,
            PieceLength = 262144,
            TorrentFileBytes = originalBytes
        };

        var serialized = ResumeDataSerializer.Serialize(resume);
        var deserialized = ResumeDataSerializer.Deserialize(serialized);

        deserialized.TorrentFileBytes.Should().NotBeNull();
        deserialized.TorrentFileBytes.Should().Equal(originalBytes);
    }

    [Fact]
    public void RoundTrip_NullTorrentFileBytes_StaysNull()
    {
        var resume = new TorrentResumeData
        {
            InfoHash = "AABBCCDD00112233AABBCCDD00112233AABBCCDD",
            Name = "test-torrent",
            PieceCount = 100,
            PieceLength = 262144,
            TorrentFileBytes = null
        };

        var serialized = ResumeDataSerializer.Serialize(resume);
        var deserialized = ResumeDataSerializer.Deserialize(serialized);

        deserialized.TorrentFileBytes.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_EmptyTorrentFileBytes_StaysNull()
    {
        var resume = new TorrentResumeData
        {
            InfoHash = "AABBCCDD00112233AABBCCDD00112233AABBCCDD",
            Name = "test-torrent",
            PieceCount = 100,
            PieceLength = 262144,
            TorrentFileBytes = Array.Empty<byte>()
        };

        var serialized = ResumeDataSerializer.Serialize(resume);
        var deserialized = ResumeDataSerializer.Deserialize(serialized);

        deserialized.TorrentFileBytes.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_LargeTorrentFileBytes_PreservedExactly()
    {
        var originalBytes = new byte[65536];
        new Random(42).NextBytes(originalBytes);

        var resume = new TorrentResumeData
        {
            InfoHash = "AABBCCDD00112233AABBCCDD00112233AABBCCDD",
            Name = "large-torrent",
            PieceCount = 5000,
            PieceLength = 4194304,
            TorrentFileBytes = originalBytes
        };

        var serialized = ResumeDataSerializer.Serialize(resume);
        var deserialized = ResumeDataSerializer.Deserialize(serialized);

        deserialized.TorrentFileBytes.Should().Equal(originalBytes);
    }
}
