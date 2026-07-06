// tests/vTorrent.CLI.Tests/Client/TokenStoreTests.cs
using System.IO;
using FluentAssertions;
using Xunit;
using vTorrent.Cli.Client;

namespace vTorrent.Cli.Tests.Client;

public class TokenStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TokenStore _store;

    public TokenStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vtorrent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new TokenStore(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, true);

    [Fact]
    public void SaveAndLoad_ReturnsStoredToken()
    {
        _store.Save("profile1", "access123", "refresh456", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        var token = _store.Load("profile1");
        token.Should().NotBeNull();
        token!.AccessToken.Should().Be("access123");
        token.RefreshToken.Should().Be("refresh456");
    }

    [Fact]
    public void Load_NonExistent_ReturnsNull()
    {
        _store.Load("missing").Should().BeNull();
    }

    [Fact]
    public void Remove_DeletesToken()
    {
        _store.Save("temp", "a", "b", 0);
        _store.Remove("temp");
        _store.Load("temp").Should().BeNull();
    }

    [Fact]
    public void IsExpired_ReturnsTrueForPastExpiry()
    {
        _store.Save("expired", "a", "b", DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds());
        var token = _store.Load("expired");
        token!.IsExpired.Should().BeTrue();
    }

    [Fact]
    public void Persistence_SurvivesReload()
    {
        _store.Save("p1", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        var reloaded = new TokenStore(_tempDir);
        reloaded.Load("p1")!.AccessToken.Should().Be("access");
    }

    [Fact]
    public void SaveApiKey_CreatesNewRecord_WhenNoExistingToken()
    {
        _store.SaveApiKey("test", "vt_abc123");
        var token = _store.Load("test");
        token.Should().NotBeNull();
        token!.ApiKey.Should().Be("vt_abc123");
        token.IsExpired.Should().BeFalse();
    }

    [Fact]
    public void SaveApiKey_PreservesExistingJwt()
    {
        _store.Save("test", "jwt_access", "jwt_refresh", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        _store.SaveApiKey("test", "vt_abc123");
        var token = _store.Load("test");
        token!.ApiKey.Should().Be("vt_abc123");
        token.AccessToken.Should().Be("jwt_access");
        token.RefreshToken.Should().Be("jwt_refresh");
    }

    [Fact]
    public void Save_PreservesExistingApiKey()
    {
        _store.SaveApiKey("test", "vt_abc123");
        _store.Save("test", "jwt_new", "refresh_new", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        var token = _store.Load("test");
        token!.ApiKey.Should().Be("vt_abc123");
        token.AccessToken.Should().Be("jwt_new");
    }

    [Fact]
    public void ClearApiKey_RemovesApiKeyOnly()
    {
        _store.Save("test", "jwt", "refresh", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        _store.SaveApiKey("test", "vt_abc123");
        _store.ClearApiKey("test");
        var token = _store.Load("test");
        token!.ApiKey.Should().BeNull();
        token.AccessToken.Should().Be("jwt");
    }

    [Fact]
    public void ClearApiKey_NoOp_WhenNoToken()
    {
        _store.ClearApiKey("nonexistent"); // Should not throw
    }
}
