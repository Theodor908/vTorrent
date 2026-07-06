using FluentAssertions;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using Xunit;
using vTorrent.Core.Network.IpFilter;
using IpFilterClass = vTorrent.Core.Network.IpFilter.IpFilter;

namespace vTorrent.Core.Tests.Network.IpFilter;

public class IpFilterLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public IpFilterLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ipfilter_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task DatFormat_ParsesCorrectly()
    {
        var path = WriteTempFile("test.dat",
            "# Comment\n" +
            "1.0.0.0 - 1.0.0.255 , 100 , Level1\n" +
            "2.0.0.0 - 2.0.0.255 , 200 , Spammers\n");

        var filter = new IpFilterClass();
        var (loaded, skipped) = await IpFilterLoader.LoadAsync(filter, path);

        loaded.Should().Be(2);
        skipped.Should().Be(0);
        filter.Access(IPAddress.Parse("1.0.0.128")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("2.0.0.128")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("3.0.0.0")).Should().Be(AccessFlags.Allowed);
    }

    [Fact]
    public async Task DatFormat_LowAccessLevel_Allowed()
    {
        var path = WriteTempFile("test.dat",
            "1.0.0.0 - 1.0.0.255 , 50 , Allowed Range\n");

        var filter = new IpFilterClass();
        var (loaded, skipped) = await IpFilterLoader.LoadAsync(filter, path);

        loaded.Should().Be(1);
        filter.Access(IPAddress.Parse("1.0.0.128")).Should().Be(AccessFlags.Allowed);
    }

    [Fact]
    public async Task P2pFormat_ParsesCorrectly()
    {
        var path = WriteTempFile("test.p2p",
            "# Comment\n" +
            "Spammers:1.0.0.0-1.0.0.255\n" +
            "Bots:2.0.0.0-2.0.0.255\n");

        var filter = new IpFilterClass();
        var (loaded, skipped) = await IpFilterLoader.LoadAsync(filter, path);

        loaded.Should().Be(2);
        filter.Access(IPAddress.Parse("1.0.0.128")).Should().Be(AccessFlags.Blocked);
        filter.Access(IPAddress.Parse("2.0.0.128")).Should().Be(AccessFlags.Blocked);
    }

    [Fact]
    public async Task MalformedLines_SkippedWithCount()
    {
        var path = WriteTempFile("test.p2p",
            "Valid:1.0.0.0-1.0.0.255\n" +
            "This is garbage\n" +
            "Also garbage without colon\n" +
            "Valid2:2.0.0.0-2.0.0.255\n");

        var filter = new IpFilterClass();
        var (loaded, skipped) = await IpFilterLoader.LoadAsync(filter, path);

        loaded.Should().Be(2);
        skipped.Should().Be(2);
    }

    [Fact]
    public async Task Comments_Ignored()
    {
        var path = WriteTempFile("test.p2p",
            "# This is a comment\n" +
            "#Another comment\n" +
            "Valid:1.0.0.0-1.0.0.255\n");

        var filter = new IpFilterClass();
        var (loaded, skipped) = await IpFilterLoader.LoadAsync(filter, path);

        loaded.Should().Be(1);
        skipped.Should().Be(0);
    }

    [Fact]
    public async Task EmptyFile_ReturnsZeroRules()
    {
        var path = WriteTempFile("test.dat", "");

        var filter = new IpFilterClass();
        var (loaded, skipped) = await IpFilterLoader.LoadAsync(filter, path);

        loaded.Should().Be(0);
        skipped.Should().Be(0);
    }

    [Fact]
    public async Task GzipFile_Decompresses()
    {
        var content = "Spammers:1.0.0.0-1.0.0.255\n";
        var path = Path.Combine(_tempDir, "test.p2p.gz");
        await using (var fs = File.Create(path))
        await using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            await gz.WriteAsync(bytes);
        }

        var filter = new IpFilterClass();
        var (loaded, skipped) = await IpFilterLoader.LoadAsync(filter, path);

        loaded.Should().Be(1);
        filter.Access(IPAddress.Parse("1.0.0.128")).Should().Be(AccessFlags.Blocked);
    }

    [Fact]
    public async Task AutoDetect_DatFormatByComma()
    {
        var path = WriteTempFile("test.txt",
            "1.0.0.0 - 1.0.0.255 , 200 , Spammers\n");

        var filter = new IpFilterClass();
        var (loaded, _) = await IpFilterLoader.LoadAsync(filter, path);

        loaded.Should().Be(1);
        filter.Access(IPAddress.Parse("1.0.0.128")).Should().Be(AccessFlags.Blocked);
    }

    private string WriteTempFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
