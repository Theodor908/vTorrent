using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Server.Models;

namespace vTorrent.Server.Auth;

public class OidcCallbackHandler
{
    private readonly JwtTokenService _jwt;
    private readonly RefreshTokenRepository _refreshRepo;
    private readonly IOptionsMonitor<ServerSettings> _serverMonitor;
    private readonly ILogger<OidcCallbackHandler> _logger;

    public OidcCallbackHandler(
        JwtTokenService jwt,
        RefreshTokenRepository refreshRepo,
        IOptionsMonitor<ServerSettings> serverMonitor,
        ILogger<OidcCallbackHandler> logger)
    {
        _jwt = jwt;
        _refreshRepo = refreshRepo;
        _serverMonitor = serverMonitor;
        _logger = logger;
    }

    /// <summary>
    /// Process an authenticated OIDC identity: verify allowed email, mint JWT + refresh token.
    /// Returns null if the identity is not authorized.
    /// </summary>
    public async Task<TokenResponse?> ProcessAsync(ClaimsPrincipal principal)
    {
        var settings = _serverMonitor.CurrentValue;
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub");
        var email = principal.FindFirstValue(ClaimTypes.Email)
                    ?? principal.FindFirstValue("email");

        if (string.IsNullOrEmpty(sub))
        {
            _logger.LogWarning("OIDC identity has no sub claim");
            return null;
        }

        // Check allowlist
        if (!string.IsNullOrEmpty(settings.OidcAllowedEmail) &&
            !string.Equals(email, settings.OidcAllowedEmail, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("OIDC login rejected: email {Email} does not match allowed {Allowed}",
                email, settings.OidcAllowedEmail);
            return null;
        }

        // Mint tokens — sub is the provider's opaque ID, NOT the email
        var accessToken = _jwt.MintAccessToken(sub);
        var refreshToken = _jwt.GenerateRefreshToken();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(settings.JwtRefreshTokenLifetimeDays).ToUnixTimeSeconds();

        await _refreshRepo.StoreAsync(refreshToken, expiresAt);

        _logger.LogInformation("OIDC login successful for {Email} (sub: {Sub})", email, sub);

        return new TokenResponse(
            accessToken,
            refreshToken,
            settings.JwtAccessTokenLifetimeMinutes * 60);
    }
}
