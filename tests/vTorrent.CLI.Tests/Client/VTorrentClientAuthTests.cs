using System;
using System.IO;
using System.Net.Http;
using FluentAssertions;
using Xunit;
using vTorrent.Cli.Client;

namespace vTorrent.Cli.Tests.Client;

public class VTorrentClientAuthTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TokenStore _store;

    public VTorrentClientAuthTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vtorrent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new TokenStore(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, true);

    [Fact]
    public void ApplyToken_PrefersApiKey_OverJwt()
    {
        _store.Save("test", "jwt_token", "refresh", long.MaxValue);
        _store.SaveApiKey("test", "vt_myapikey");

        using var http = new HttpClient();
        using var client = new VTorrentClient(http, _store, "test");

        http.DefaultRequestHeaders.Contains("X-API-Key").Should().BeTrue();
        http.DefaultRequestHeaders.Authorization.Should().BeNull();
    }

    [Fact]
    public void ApplyToken_UsesJwt_WhenNoApiKey()
    {
        _store.Save("test", "jwt_token", "refresh", long.MaxValue);

        using var http = new HttpClient();
        using var client = new VTorrentClient(http, _store, "test");

        http.DefaultRequestHeaders.Authorization.Should().NotBeNull();
        http.DefaultRequestHeaders.Authorization!.Scheme.Should().Be("Bearer");
        http.DefaultRequestHeaders.Contains("X-API-Key").Should().BeFalse();
    }

    [Fact]
    public void ApplyToken_ForceJwt_OverridesApiKeyPreference()
    {
        _store.Save("test", "jwt_token", "refresh", long.MaxValue);
        _store.SaveApiKey("test", "vt_myapikey");

        using var http = new HttpClient();
        using var client = new VTorrentClient(http, _store, "test", forceJwt: true);

        http.DefaultRequestHeaders.Authorization.Should().NotBeNull();
        http.DefaultRequestHeaders.Authorization!.Scheme.Should().Be("Bearer");
        http.DefaultRequestHeaders.Contains("X-API-Key").Should().BeFalse();
    }
}
