using FluentAssertions;
using Xunit;
using vTorrent.Cli.Client;

namespace vTorrent.Cli.Tests.Client;

public class StoredTokenTests
{
    [Fact]
    public void IsExpiringSoon_ReturnsTrueWhenWithinThreshold()
    {
        var token = new StoredToken
        {
            AccessToken = "test",
            RefreshToken = "test",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3).ToUnixTimeSeconds()
        };
        token.IsExpiringSoon.Should().BeTrue();
    }

    [Fact]
    public void IsExpiringSoon_ReturnsFalseWhenFarFromExpiry()
    {
        var token = new StoredToken
        {
            AccessToken = "test",
            RefreshToken = "test",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        };
        token.IsExpiringSoon.Should().BeFalse();
    }

    [Fact]
    public void IsExpiringSoon_ReturnsFalseWhenAlreadyExpired()
    {
        var token = new StoredToken
        {
            AccessToken = "test",
            RefreshToken = "test",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()
        };
        token.IsExpiringSoon.Should().BeFalse();
    }

    [Fact]
    public void ApiKey_DefaultsToNull()
    {
        var token = new StoredToken { AccessToken = "test", RefreshToken = "test", ExpiresAt = 0 };
        token.ApiKey.Should().BeNull();
    }

    [Fact]
    public void ApiKeyOnly_IsNotExpired_WithMaxValueSentinel()
    {
        var token = new StoredToken { ApiKey = "vt_abc123", ExpiresAt = long.MaxValue };
        token.IsExpired.Should().BeFalse();
        token.IsExpiringSoon.Should().BeFalse();
    }

    [Fact]
    public void HasApiKey_ReturnsTrueWhenSet()
    {
        var token = new StoredToken { ApiKey = "vt_abc123", ExpiresAt = long.MaxValue };
        token.HasApiKey.Should().BeTrue();
    }

    [Fact]
    public void HasApiKey_ReturnsFalseWhenNull()
    {
        var token = new StoredToken { AccessToken = "jwt", ExpiresAt = long.MaxValue };
        token.HasApiKey.Should().BeFalse();
    }
}
