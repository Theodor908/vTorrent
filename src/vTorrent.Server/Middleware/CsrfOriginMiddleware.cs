using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Server.Middleware;

public class CsrfOriginMiddleware
{
    private readonly RequestDelegate _next;

    public CsrfOriginMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IOptionsMonitor<ServerSettings> serverMonitor, ILogger<CsrfOriginMiddleware> logger)
    {
        var settings = serverMonitor.CurrentValue;

        if (!settings.EnableCsrfProtection)
        {
            await _next(context);
            return;
        }

        if (!CsrfValidator.IsValidOrigin(context, settings, strictMode: false, logger))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            var error = settings.VerboseSecurityErrors
                ? new { error = "CSRF origin validation failed", code = "CSRF_ORIGIN_MISMATCH" }
                : new { error = "Forbidden", code = "SECURITY_VIOLATION" };
            await context.Response.WriteAsJsonAsync(error);
            return;
        }

        await _next(context);
    }
}
