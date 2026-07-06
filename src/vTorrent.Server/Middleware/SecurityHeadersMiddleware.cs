using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Server.Middleware;

public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IOptionsMonitor<ServerSettings> serverMonitor)
    {
        var settings = serverMonitor.CurrentValue;
        var headers = context.Response.Headers;

        if (settings.EnableSecurityHeaders)
        {
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";
        }

        if (settings.EnableClickjackingProtection)
        {
            headers["X-Frame-Options"] = "DENY";
        }

        if (settings.EnableSecurityHeaders || settings.EnableClickjackingProtection)
        {
            var csp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' wss:";
            if (settings.EnableClickjackingProtection)
                csp += "; frame-ancestors 'none'";
            headers["Content-Security-Policy"] = csp;
        }

        if (context.Request.IsHttps)
        {
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }

        await _next(context);
    }
}
