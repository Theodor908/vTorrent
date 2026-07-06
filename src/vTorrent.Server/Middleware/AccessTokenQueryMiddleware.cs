namespace vTorrent.Server.Middleware;

/// <summary>
/// Extracts ?access_token from query string and sets Authorization header.
/// Required for SignalR WebSocket connections where headers cannot be set on WS upgrade.
/// </summary>
public class AccessTokenQueryMiddleware
{
    private readonly RequestDelegate _next;

    public AccessTokenQueryMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var accessToken = context.Request.Query["access_token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(accessToken) &&
            string.IsNullOrEmpty(context.Request.Headers.Authorization))
        {
            context.Request.Headers.Authorization = $"Bearer {accessToken}";
        }
        await _next(context);
    }
}
