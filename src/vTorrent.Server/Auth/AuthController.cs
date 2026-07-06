using System.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Settings;
using vTorrent.Server.Auth;
using vTorrent.Server.Models;

namespace vTorrent.Server.Auth;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly JwtTokenService _jwt;
    private readonly RefreshTokenRepository _refreshRepo;
    private readonly PasswordHasher _passwordHasher;
    private readonly IOptionsMonitor<ServerSettings> _serverMonitor;
    private readonly SettingsManager _settingsManager;
    private readonly ApiKeyRepository _apiKeyRepo;

    public AuthController(
        JwtTokenService jwt,
        RefreshTokenRepository refreshRepo,
        PasswordHasher passwordHasher,
        IOptionsMonitor<ServerSettings> serverMonitor,
        SettingsManager settingsManager,
        ApiKeyRepository apiKeyRepo)
    {
        _jwt = jwt;
        _refreshRepo = refreshRepo;
        _passwordHasher = passwordHasher;
        _serverMonitor = serverMonitor;
        _settingsManager = settingsManager;
        _apiKeyRepo = apiKeyRepo;
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var settings = _serverMonitor.CurrentValue;

        if (request.Username != settings.LocalUsername ||
            !_passwordHasher.Verify(request.Password, settings.LocalPasswordHash))
        {
            return Unauthorized(new { error = "Invalid credentials" });
        }

        var accessToken = _jwt.MintAccessToken(settings.LocalUsername);
        var refreshToken = _jwt.GenerateRefreshToken();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(settings.JwtRefreshTokenLifetimeDays).ToUnixTimeSeconds();

        await _refreshRepo.StoreAsync(refreshToken, expiresAt);

        return Ok(new TokenResponse(
            accessToken,
            refreshToken,
            settings.JwtAccessTokenLifetimeMinutes * 60));
    }

    [HttpPost("refresh")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
    {
        var existing = await _refreshRepo.GetAsync(request.RefreshToken);

        if (existing == null || existing.IsExpired)
            return Unauthorized(new { error = "Invalid or expired refresh token" });

        if (existing.IsRevoked)
        {
            // Replay detected — revoke entire token family (single-user = all tokens)
            await _refreshRepo.RevokeAllAsync();
            return Unauthorized(new { error = "Token reuse detected. All sessions revoked." });
        }

        // Rotate: revoke old, issue new
        var newRefreshToken = _jwt.GenerateRefreshToken();
        var revoked = await _refreshRepo.RevokeAsync(request.RefreshToken, replacedBy: newRefreshToken);

        if (!revoked)
        {
            // Concurrent request consumed the token first — not an attack, just a race
            return Unauthorized(new { error = "Token already consumed" });
        }

        var settings = _serverMonitor.CurrentValue;
        var expiresAt = DateTimeOffset.UtcNow.AddDays(settings.JwtRefreshTokenLifetimeDays).ToUnixTimeSeconds();
        await _refreshRepo.StoreAsync(newRefreshToken, expiresAt);

        var accessToken = _jwt.MintAccessToken(settings.LocalUsername);

        return Ok(new TokenResponse(
            accessToken,
            newRefreshToken,
            settings.JwtAccessTokenLifetimeMinutes * 60));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest request)
    {
        // Anonymous — refresh token is its own credential
        await _refreshRepo.RevokeAsync(request.RefreshToken);
        return NoContent();
    }

    [Authorize]
    [HttpPost("change-password")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var settings = _serverMonitor.CurrentValue;

        // Verify current password
        if (!_passwordHasher.Verify(request.CurrentPassword, settings.LocalPasswordHash))
            return Unauthorized(new ErrorResponse("Current password is incorrect", "INVALID_PASSWORD"));

        // Validate new password
        if (string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(new ErrorResponse("New password cannot be empty", "VALIDATION_ERROR"));

        if (request.NewPassword.Length < 6)
            return BadRequest(new ErrorResponse("New password must be at least 6 characters", "VALIDATION_ERROR"));

        // Hash and save — plaintext never stored
        var newHash = _passwordHasher.Hash(request.NewPassword);
        await _settingsManager.UpdateAndSaveAsync(gs =>
        {
            gs.Server.LocalPasswordHash = newHash;
        });

        // Revoke all existing refresh tokens — force re-login with new password
        await _refreshRepo.RevokeAllAsync();

        return Ok(new { message = "Password changed. All sessions revoked — please log in again." });
    }

    [HttpGet("oidc/login")]
    public IActionResult OidcLogin()
    {
        if (string.IsNullOrEmpty(_serverMonitor.CurrentValue.OidcAuthority))
            return BadRequest(new { error = "OIDC is not configured" });

        return Challenge(new AuthenticationProperties { RedirectUri = "/auth/oidc/callback" }, "oidc");
    }

    [HttpGet("oidc/callback")]
    public async Task<IActionResult> OidcCallback([FromServices] OidcCallbackHandler handler)
    {
        var result = await HttpContext.AuthenticateAsync("oidc");
        if (!result.Succeeded || result.Principal == null)
            return Unauthorized(new { error = "OIDC authentication failed" });

        var tokenResponse = await handler.ProcessAsync(result.Principal);
        if (tokenResponse == null)
            return Forbid();

        return Ok(tokenResponse);
    }

    [HttpGet("oidc/error")]
    public IActionResult OidcError()
    {
        return BadRequest(new { error = "OIDC provider authentication failed. Check server logs." });
    }

    [Authorize]
    [HttpGet("api-keys")]
    public async Task<IActionResult> ListApiKeys()
    {
        if (!_serverMonitor.CurrentValue.ApiKeysEnabled)
            return BadRequest(new ErrorResponse("API keys are not enabled", "API_KEYS_DISABLED"));

        var keys = await _apiKeyRepo.ListAsync();
        var items = keys.Select(k => new ApiKeyListItem(k.KeyPrefix, k.Label, k.CreatedAt, k.LastUsed, k.IsRevoked));
        return Ok(items);
    }

    [Authorize]
    [HttpPost("api-keys")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> CreateApiKey([FromBody] CreateApiKeyRequest request)
    {
        if (!_serverMonitor.CurrentValue.ApiKeysEnabled)
            return BadRequest(new ErrorResponse("API keys are not enabled", "API_KEYS_DISABLED"));

        if (string.IsNullOrWhiteSpace(request.Label))
            return BadRequest(new ErrorResponse("Label is required", "VALIDATION_ERROR"));

        var (rawKey, info) = await _apiKeyRepo.CreateAsync(request.Label.Trim());
        return Ok(new CreateApiKeyResponse(rawKey, info.KeyPrefix, info.Label, info.CreatedAt));
    }

    [Authorize]
    [HttpDelete("api-keys/{keyPrefix}")]
    public async Task<IActionResult> RevokeApiKey(string keyPrefix)
    {
        if (!_serverMonitor.CurrentValue.ApiKeysEnabled)
            return BadRequest(new ErrorResponse("API keys are not enabled", "API_KEYS_DISABLED"));

        var revoked = await _apiKeyRepo.RevokeByPrefixAsync(keyPrefix);
        if (!revoked)
            return NotFound(new ErrorResponse("API key not found or already revoked", "RESOURCE_NOT_FOUND"));

        return NoContent();
    }
}
