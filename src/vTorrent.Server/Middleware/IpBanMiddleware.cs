using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Server.Auth;

namespace vTorrent.Server.Middleware;

public class IpBanMiddleware
{
    private readonly RequestDelegate _next;

    public IpBanMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IpBanTracker banTracker,
        IOptionsMonitor<ServerSettings> serverMonitor,
        ILogger<IpBanMiddleware> logger)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip == null)
        {
            await _next(context);
            return;
        }

        // Request path: check if banned
        if (banTracker.IsBanned(ip))
        {
            var remaining = banTracker.GetRemainingBan(ip);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            if (remaining.HasValue)
                context.Response.Headers["Retry-After"] = ((int)remaining.Value.TotalSeconds).ToString();

            var settings = serverMonitor.CurrentValue;
            var error = settings.VerboseSecurityErrors
                ? new { error = "IP temporarily banned", code = "IP_BANNED" }
                : new { error = "Forbidden", code = "SECURITY_VIOLATION" };
            await context.Response.WriteAsJsonAsync(error);
            return;
        }

        await _next(context);

        // Response path: track auth endpoint failures/successes
        var path = context.Request.Path.Value ?? "";
        var isAuthEndpoint = path.Equals("/auth/login", StringComparison.OrdinalIgnoreCase)
                          || path.Equals("/auth/refresh", StringComparison.OrdinalIgnoreCase);

        if (!isAuthEndpoint)
            return;

        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
        {
            banTracker.RecordFailure(ip);
        }
        else if (context.Response.StatusCode == StatusCodes.Status200OK)
        {
            banTracker.RecordSuccess(ip);
        }
    }
}
