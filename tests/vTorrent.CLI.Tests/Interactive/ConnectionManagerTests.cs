using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using vTorrent.Cli.Interactive;
using vTorrent.Cli.Profiles;
using vTorrent.Cli.Client;

namespace vTorrent.Cli.Tests.Interactive;

public class ConnectionManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProfileManager _profileManager;
    private readonly TokenStore _tokenStore;

    public ConnectionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vtorrent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _profileManager = new ProfileManager(_tempDir);
        _tokenStore = new TokenStore(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, true);

    [Fact]
    public void IsConnected_DefaultsFalse()
    {
        var mgr = new ConnectionManager(_profileManager, _tokenStore);
        mgr.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void ActiveProfile_DefaultsNull()
    {
        var mgr = new ConnectionManager(_profileManager, _tokenStore);
        mgr.ActiveProfile.Should().BeNull();
    }

    [Fact]
    public async Task CheckHealthAsync_NoProfile_ReturnsFalse()
    {
        var mgr = new ConnectionManager(_profileManager, _tokenStore);
        var result = await mgr.CheckHealthAsync("nonexistent");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ConnectAsync_NoProfile_ReturnsProfileNotFound()
    {
        var mgr = new ConnectionManager(_profileManager, _tokenStore);
        var result = await mgr.ConnectAsync("nonexistent");
        result.Should().Be(ConnectResult.ProfileNotFound);
    }
}
