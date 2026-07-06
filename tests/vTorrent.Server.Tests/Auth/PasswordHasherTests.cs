using FluentAssertions;
using Xunit;
using vTorrent.Server.Auth;

namespace vTorrent.Server.Tests.Auth;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Hash_ReturnsNonEmptyBcryptString()
    {
        var hash = _hasher.Hash("test123");
        hash.Should().StartWith("$2");
        hash.Length.Should().BeGreaterThan(50);
    }

    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        var hash = _hasher.Hash("mypassword");
        _hasher.Verify("mypassword", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hash = _hasher.Hash("mypassword");
        _hasher.Verify("wrongpassword", hash).Should().BeFalse();
    }

    [Fact]
    public void Verify_InvalidHash_ReturnsFalse()
    {
        _hasher.Verify("anything", "not-a-hash").Should().BeFalse();
    }

    [Fact]
    public void Verify_EmptyHash_ReturnsFalse()
    {
        _hasher.Verify("anything", "").Should().BeFalse();
    }
}
