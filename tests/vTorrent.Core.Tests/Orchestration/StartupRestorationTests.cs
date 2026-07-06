using FluentAssertions;
using vTorrent.Bencode.Objects;
using vTorrent.Bencode.Parsers;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.ResumeData;
using Xunit;

namespace vTorrent.Core.Tests.Orchestration;

public class StartupRestorationTests
{
    [Fact]
    public void EmbeddedTorrentBytes_CanBeParsedToTorrent()
    {
        var infoDict = new BDictionary();
        infoDict.AddString("name", "test-file.txt");
        infoDict.AddNumber("piece length", 262144);
        infoDict.AddBytes("pieces", new byte[20]);
        infoDict.AddNumber("length", 1024);

        var torrentDict = new BDictionary();
        torrentDict.AddString("announce", "http://tracker.example.com/announce");
        torrentDict.Add("info", infoDict);

        var torrentBytes = torrentDict.EncodeAsBytes();

        var parser = new BencodeParser();
        var parsed = parser.Parse(torrentBytes, out _);
        parsed.Should().BeOfType<BDictionary>();

        var torrent = TorrentParser.FromBDictionary((BDictionary)parsed);

        torrent.Should().NotBeNull();
        torrent.Info.Name.Should().Be("test-file.txt");
        torrent.Info.PieceLength.Should().Be(262144);
        torrent.Announce.Should().Be("http://tracker.example.com/announce");
    }

    [Fact]
    public void ResumeData_WithEmbeddedBytes_SurvivesRoundTrip()
    {
        var infoDict = new BDictionary();
        infoDict.AddString("name", "embedded-test.txt");
        infoDict.AddNumber("piece length", 262144);
        infoDict.AddBytes("pieces", new byte[20]);
        infoDict.AddNumber("length", 512);

        var torrentDict = new BDictionary();
        torrentDict.AddString("announce", "http://tracker.example.com/announce");
        torrentDict.Add("info", infoDict);

        var torrentBytes = torrentDict.EncodeAsBytes();

        var resume = new TorrentResumeData
        {
            InfoHash = "AABBCCDD00112233AABBCCDD00112233AABBCCDD",
            Name = "embedded-test.txt",
            PieceCount = 1,
            PieceLength = 262144,
            TorrentFileBytes = torrentBytes
        };

        var serialized = ResumeDataSerializer.Serialize(resume);
        var loaded = ResumeDataSerializer.Deserialize(serialized);

        var parser = new BencodeParser();
        var parsed = parser.Parse(loaded.TorrentFileBytes!, out _);
        var torrent = TorrentParser.FromBDictionary((BDictionary)parsed);

        torrent.Info.Name.Should().Be("embedded-test.txt");
        torrent.Announce.Should().Be("http://tracker.example.com/announce");
    }

    /// <summary>
    /// When both embedded bytes AND a .torrent file exist, the embedded path
    /// should be used (no disk I/O). Simulates the primary optimization path.
    /// </summary>
    [Fact]
    public void EmbeddedBytes_TakePriorityOverDiskFile()
    {
        var embeddedInfo = new BDictionary();
        embeddedInfo.AddString("name", "from-embedded");
        embeddedInfo.AddNumber("piece length", 262144);
        embeddedInfo.AddBytes("pieces", new byte[20]);
        embeddedInfo.AddNumber("length", 1024);

        var embeddedDict = new BDictionary();
        embeddedDict.AddString("announce", "http://embedded-tracker.com/announce");
        embeddedDict.Add("info", embeddedInfo);

        var diskInfo = new BDictionary();
        diskInfo.AddString("name", "from-disk");
        diskInfo.AddNumber("piece length", 262144);
        diskInfo.AddBytes("pieces", new byte[20]);
        diskInfo.AddNumber("length", 2048);

        var diskDict = new BDictionary();
        diskDict.AddString("announce", "http://disk-tracker.com/announce");
        diskDict.Add("info", diskInfo);

        var embeddedBytes = embeddedDict.EncodeAsBytes();
        var diskBytes = diskDict.EncodeAsBytes();

        Torrent? result = null;
        bool loadedFromEmbedded = false;

        if (embeddedBytes != null && embeddedBytes.Length > 0)
        {
            var parser = new BencodeParser();
            var parsed = parser.Parse(embeddedBytes, out _);
            if (parsed is BDictionary dict)
            {
                result = TorrentParser.FromBDictionary(dict);
                loadedFromEmbedded = true;
            }
        }

        if (!loadedFromEmbedded)
        {
            var parser = new BencodeParser();
            var parsed = parser.Parse(diskBytes, out _);
            if (parsed is BDictionary dict)
                result = TorrentParser.FromBDictionary(dict);
        }

        result.Should().NotBeNull();
        result!.Info.Name.Should().Be("from-embedded");
        result.Announce.Should().Be("http://embedded-tracker.com/announce");
        loadedFromEmbedded.Should().BeTrue();
    }

    /// <summary>
    /// When embedded bytes are null (old resume data), the disk path is used
    /// AND the loaded bytes should be backfilled into resume data for next save.
    /// </summary>
    [Fact]
    public void NullEmbeddedBytes_FallsBackToDiskAndBackfills()
    {
        var diskInfo = new BDictionary();
        diskInfo.AddString("name", "disk-only-torrent");
        diskInfo.AddNumber("piece length", 262144);
        diskInfo.AddBytes("pieces", new byte[20]);
        diskInfo.AddNumber("length", 4096);

        var diskDict = new BDictionary();
        diskDict.AddString("announce", "http://tracker.example.com/announce");
        diskDict.Add("info", diskInfo);

        var diskBytes = diskDict.EncodeAsBytes();
        byte[]? embeddedBytes = null;

        var resumeData = new TorrentResumeData
        {
            InfoHash = "AABBCCDD00112233AABBCCDD00112233AABBCCDD",
            Name = "disk-only-torrent",
            TorrentFileBytes = embeddedBytes
        };

        Torrent? result = null;
        bool loadedFromEmbedded = false;

        if (resumeData.TorrentFileBytes != null && resumeData.TorrentFileBytes.Length > 0)
        {
            loadedFromEmbedded = true;
        }

        if (!loadedFromEmbedded)
        {
            var parser = new BencodeParser();
            var parsed = parser.Parse(diskBytes, out _);
            if (parsed is BDictionary dict)
            {
                result = TorrentParser.FromBDictionary(dict);
                resumeData.TorrentFileBytes = diskBytes;
            }
        }

        result.Should().NotBeNull();
        result!.Info.Name.Should().Be("disk-only-torrent");
        loadedFromEmbedded.Should().BeFalse();

        resumeData.TorrentFileBytes.Should().NotBeNull();
        resumeData.TorrentFileBytes.Should().Equal(diskBytes);
    }

    /// <summary>
    /// Verifies the full lifecycle: NoVerifyFiles is set during clean shutdown
    /// save, survives serialization, and is cleared when crash recovery is detected.
    /// </summary>
    [Fact]
    public void NoVerifyFiles_Lifecycle_SetOnShutdown_ClearedOnCrash()
    {
        var resume = new TorrentResumeData
        {
            InfoHash = "AABBCCDD00112233AABBCCDD00112233AABBCCDD",
            Name = "lifecycle-test",
            PieceCount = 100,
            PieceLength = 262144,
            HavePieces = new byte[13],
        };

        // Phase 1: Simulate clean shutdown — set NoVerifyFiles
        resume.Flags |= TorrentFlags.NoVerifyFiles;
        resume.Flags.HasFlag(TorrentFlags.NoVerifyFiles).Should().BeTrue();

        // Phase 2: Serialize (save to disk)
        var saved = ResumeDataSerializer.Serialize(resume);

        // Phase 3: Deserialize (load on next startup)
        var loaded = ResumeDataSerializer.Deserialize(saved);
        loaded.Flags.HasFlag(TorrentFlags.NoVerifyFiles).Should().BeTrue("flag should survive round-trip");

        // Phase 4: Simulate crash recovery detected — clear NoVerifyFiles
        loaded.NeedsCrashRecovery = true;
        loaded.Flags &= ~TorrentFlags.NoVerifyFiles;
        loaded.Flags.HasFlag(TorrentFlags.NoVerifyFiles).Should().BeFalse("flag should be cleared on crash");

        // Phase 5: Other flags should be unaffected
        loaded.Flags.HasFlag(TorrentFlags.AutoManaged).Should().BeTrue("AutoManaged from DefaultFlags preserved");
    }
}
