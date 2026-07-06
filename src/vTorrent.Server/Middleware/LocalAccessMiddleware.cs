using System;
using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Server.Middleware;

/// <summary>
/// When AllowLocalAccess is enabled, requests from loopback addresses
/// (127.0.0.1, ::1) are given an authenticated identity, bypassing JWT.
/// When EnableSubnetAuthBypass is enabled, requests from configured subnets
/// are also given an authenticated identity after CSRF validation.
/// </summary>
public class LocalAccessMiddleware
{
    private readonly RequestDelegate _next;

    public LocalAccessMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IOptionsMonitor<ServerSettings> serverMonitor, ILogger<LocalAccessMiddleware> logger)
    {
        var settings = serverMonitor.CurrentValue;
        var ip = context.Connection.RemoteIpAddress;

        if (context.User.Identity?.IsAuthenticated != true && ip != null)
        {
            bool shouldInject = false;

            if (settings.AllowLocalAccess && IsLoopback(ip))
            {
                shouldInject = true;
            }
            else if (settings.EnableSubnetAuthBypass && !string.IsNullOrWhiteSpace(settings.AuthBypassSubnets))
            {
                var mapped = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
                var subnets = settings.AuthBypassSubnets.Split(';');

                foreach (var entry in subnets)
                {
                    var trimmed = entry.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                        continue;

                    if (IPNetwork.TryParse(trimmed, out var network) && network.Contains(mapped))
                    {
                        if (settings.EnableCsrfProtection &&
                            !CsrfValidator.IsValidOrigin(context, settings, strictMode: true, logger))
                        {
                            context.Response.StatusCode = 403;
                            var message = settings.VerboseSecurityErrors
                                ? "CSRF validation failed for subnet bypass request."
                                : "Forbidden.";
                            await context.Response.WriteAsync(message);
                            return;
                        }

                        shouldInject = true;
                        break;
                    }
                }
            }

            if (shouldInject)
            {
                var claims = new[] { new Claim(ClaimTypes.Name, "local") };
                var identity = new ClaimsIdentity(claims, "LocalAccess");
                context.User = new ClaimsPrincipal(identity);
            }
        }

        await _next(context);
    }

    private static bool IsLoopback(IPAddress? address)
    {
        if (address == null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        // Handle IPv4-mapped IPv6 (::ffff:127.0.0.1)
        if (address.IsIPv4MappedToIPv6)
            return IPAddress.IsLoopback(address.MapToIPv4());
        return false;
    }
}
