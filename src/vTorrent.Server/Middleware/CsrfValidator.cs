using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Server.Middleware;

public static class CsrfValidator
{
    public static bool IsValidOrigin(HttpContext context, ServerSettings settings, bool strictMode, ILogger logger)
    {
        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            return true;
        if (context.Request.Headers.ContainsKey("X-API-Key"))
            return true;

        var secFetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (!string.IsNullOrEmpty(secFetchSite))
        {
            return secFetchSite switch
            {
                "same-origin" => true,
                "same-site" => true,
                "none" => true,
                "cross-site" => LogAndReject(context, logger, "Sec-Fetch-Site: cross-site"),
                _ => true
            };
        }

        var targetOrigin = GetTargetOrigin(context, settings);

        var origin = context.Request.Headers["Origin"].ToString();
        if (!string.IsNullOrEmpty(origin) && origin != "null")
        {
            if (!OriginMatchesTarget(origin, targetOrigin))
                return LogAndReject(context, logger, $"Origin mismatch: {origin} vs {targetOrigin}");
            return true;
        }

        var referer = context.Request.Headers["Referer"].ToString();
        if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
        {
            var refOrigin = $"{refUri.Scheme}://{refUri.Host}" + (refUri.IsDefaultPort ? "" : $":{refUri.Port}");
            if (!OriginMatchesTarget(refOrigin, targetOrigin))
                return LogAndReject(context, logger, $"Referer mismatch: {refOrigin} vs {targetOrigin}");
            return true;
        }

        if (strictMode)
            return LogAndReject(context, logger, "Both Origin and Referer absent (strict mode)");

        return true;
    }

    private static string GetTargetOrigin(HttpContext context, ServerSettings settings)
    {
        var host = "";
        var scheme = "";

        if (settings.EnableReverseProxySupport)
        {
            host = context.Request.Headers["X-Forwarded-Host"].ToString();
            scheme = context.Request.Headers["X-Forwarded-Proto"].ToString();
        }

        if (string.IsNullOrEmpty(host))
            host = context.Request.Host.ToString();
        if (string.IsNullOrEmpty(scheme))
            scheme = context.Request.Scheme;

        return $"{scheme}://{host}";
    }

    private static bool OriginMatchesTarget(string origin, string target)
    {
        return string.Equals(origin.TrimEnd('/'), target.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool LogAndReject(HttpContext context, ILogger logger, string reason)
    {
        logger.LogWarning("CSRF rejection: {Reason} | IP={Ip} | Origin={Origin} | Referer={Referer} | Sec-Fetch-Site={SecFetch}",
            reason, context.Connection.RemoteIpAddress,
            context.Request.Headers["Origin"].ToString(),
            context.Request.Headers["Referer"].ToString(),
            context.Request.Headers["Sec-Fetch-Site"].ToString());
        return false;
    }
}
