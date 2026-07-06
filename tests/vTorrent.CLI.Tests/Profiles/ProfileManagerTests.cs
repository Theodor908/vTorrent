// tests/vTorrent.CLI.Tests/Profiles/ProfileManagerTests.cs
using System.IO;
using FluentAssertions;
using Xunit;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Tests.Profiles;

public class ProfileManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProfileManager _manager;

    public ProfileManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vtorrent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _manager = new ProfileManager(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, true);

    [Fact]
    public void AddAndGet_ReturnsProfile()
    {
        _manager.Add("test", "localhost:8080", https: true, insecure: false, "admin");
        var profile = _manager.Get("test");
        profile.Should().NotBeNull();
        profile!.Host.Should().Be("localhost:8080");
        profile.Username.Should().Be("admin");
    }

    [Fact]
    public void GetDefault_ReturnsFirstAdded()
    {
        _manager.Add("first", "host1:8080", true, false, "admin");
        _manager.GetDefault().Should().Be("first");
    }

    [Fact]
    public void SetDefault_ChangesDefault()
    {
        _manager.Add("a", "host1:8080", true, false, "admin");
        _manager.Add("b", "host2:8080", true, false, "admin");
        _manager.SetDefault("b");
        _manager.GetDefault().Should().Be("b");
    }

    [Fact]
    public void Remove_DeletesProfile()
    {
        _manager.Add("temp", "host:8080", true, false, "admin");
        _manager.Remove("temp");
        _manager.Get("temp").Should().BeNull();
    }

    [Fact]
    public void ListAll_ReturnsAllProfiles()
    {
        _manager.Add("a", "host1:8080", true, false, "admin");
        _manager.Add("b", "host2:8080", true, false, "admin");
        _manager.ListAll().Should().HaveCount(2);
    }

    [Fact]
    public void Persistence_SurvivesReload()
    {
        _manager.Add("persisted", "host:8080", true, true, "user");
        var reloaded = new ProfileManager(_tempDir);
        reloaded.Get("persisted")!.Host.Should().Be("host:8080");
    }
}
