using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Xunit;
using vTorrent.Abstractions.Settings;
using vTorrent.Server.Auth;

namespace vTorrent.Server.Tests.Auth;

public class JwtTokenServiceTests
{
    private readonly SettingsMonitor<ServerSettings> _monitor;
    private readonly JwtTokenService _service;

    public JwtTokenServiceTests()
    {
        _monitor = new SettingsMonitor<ServerSettings>();
        _monitor.Update(new ServerSettings
        {
            JwtSecret = JwtTokenService.GenerateJwtSecret(),
            JwtAccessTokenLifetimeMinutes = 15
        });
        _service = new JwtTokenService(_monitor);
    }

    [Fact]
    public void MintAccessToken_ReturnsValidJwt()
    {
        var token = _service.MintAccessToken("admin");
        token.Should().NotBeNullOrEmpty();
        token.Split('.').Should().HaveCount(3);
    }

    [Fact]
    public void MintAccessToken_ContainsCorrectSubject()
    {
        var token = _service.MintAccessToken("testuser");
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        jwt.Subject.Should().Be("testuser");
    }

    [Fact]
    public void MintAccessToken_ExpiresApproximatelyCorrectly()
    {
        var token = _service.MintAccessToken("admin");
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        var expectedExpiry = DateTime.UtcNow.AddMinutes(15);
        jwt.ValidTo.Should().BeCloseTo(expectedExpiry, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void GenerateRefreshToken_Returns64CharHex()
    {
        var token = _service.GenerateRefreshToken();
        token.Should().HaveLength(64);
        token.Should().MatchRegex("^[0-9A-F]+$");
    }

    [Fact]
    public void GenerateJwtSecret_Returns64CharHex()
    {
        var secret = JwtTokenService.GenerateJwtSecret();
        secret.Should().HaveLength(64);
        secret.Should().MatchRegex("^[0-9A-F]+$");
    }
}
