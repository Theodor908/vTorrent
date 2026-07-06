using System;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Server.Auth;

namespace vTorrent.Server.Middleware;

public class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ConcurrentDictionary<string, long> _lastUsedCache = new();

    public ApiKeyAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IOptionsMonitor<ServerSettings> serverMonitor,
        ApiKeyRepository apiKeyRepo,
        ILogger<ApiKeyAuthMiddleware> logger)
    {
        var settings = serverMonitor.CurrentValue;

        if (!settings.ApiKeysEnabled)
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        var apiKeyHeader = context.Request.Headers["X-API-Key"].ToString();
        if (string.IsNullOrEmpty(apiKeyHeader))
        {
            await _next(context);
            return;
        }

        var info = await apiKeyRepo.GetInfoAsync(apiKeyHeader);
        if (info == null)
        {
            logger.LogWarning("Invalid API key attempt from {Ip}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid API key", code = "INVALID_API_KEY" });
            return;
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, $"apikey:{info.Label}"),
            new Claim("auth_method", "apikey")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));

        // Per-key debounced last_used update
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var keyHash = ApiKeyRepository.HashKey(apiKeyHeader);
        var lastUpdate = _lastUsedCache.GetOrAdd(keyHash, 0L);
        if (now - lastUpdate >= 60)
        {
            _lastUsedCache[keyHash] = now;
            _ = Task.Run(async () =>
            {
                try { await apiKeyRepo.UpdateLastUsedAsync(keyHash, now); }
                catch (Exception ex) { logger.LogDebug(ex, "Failed to update last_used for API key"); }
            });
        }

        await _next(context);
    }
}
