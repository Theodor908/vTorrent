namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Configuration for the vTorrent.Server daemon (SignalR + auth).
/// Server is opt-in — disabled by default.
/// </summary>
public class ServerSettings
{
    // --- Server ---

    /// <summary>Enable the web server (SignalR hub + auth endpoints). Default: false (opt-in).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>HTTP listen port for Kestrel. Default: 8080.</summary>
    public int ListenPort { get; set; } = 8080;

    /// <summary>
    /// Bind address. "127.0.0.1" for localhost only (default, safest).
    /// Change to "0.0.0.0" for network/remote access (requires firewall awareness).
    /// </summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>Enable HTTPS. When true and no cert is configured, a self-signed cert is auto-generated.</summary>
    public bool EnableHttps { get; set; } = true;

    /// <summary>Path to PFX/PEM certificate file for HTTPS. Empty = uses ASP.NET Core development certificate (run 'dotnet dev-certs https' to generate).</summary>
    public string HttpsCertPath { get; set; } = "";

    /// <summary>
    /// Password for the HTTPS certificate file.
    /// WARNING: Stored in plaintext in global.json. Use a passwordless certificate where possible.
    /// </summary>
    public string HttpsCertPassword { get; set; } = "";

    // --- Local Auth ---

    /// <summary>Local login username. Single-user model.</summary>
    public string LocalUsername { get; set; } = "admin";

    /// <summary>Bcrypt hash of the local password. Empty = not configured (server won't start).</summary>
    public string LocalPasswordHash { get; set; } = "$2a$12$BZ28/xgKOcCn6XGCLPI4hufts064CnlwRgabtoD6imf9TMVkhVDKa";

    /// <summary>
    /// When true, requests from localhost (127.0.0.1 / ::1) bypass JWT authentication.
    /// Safe when ListenAddress is 127.0.0.1 (default). Dangerous if ListenAddress is 0.0.0.0.
    /// </summary>
    public bool AllowLocalAccess { get; set; } = true;

    // --- JWT ---

    /// <summary>HMAC-SHA256 signing key for JWT tokens. Auto-generated on first enable if empty.</summary>
    public string JwtSecret { get; set; } = "";

    /// <summary>JWT access token lifetime in minutes. Default: 15.</summary>
    public int JwtAccessTokenLifetimeMinutes { get; set; } = 15;

    /// <summary>Refresh token lifetime in days. Default: 30.</summary>
    public int JwtRefreshTokenLifetimeDays { get; set; } = 30;

    // --- OIDC (optional — disabled when Authority is empty) ---

    /// <summary>OIDC authority URL (e.g., "https://accounts.google.com"). Empty = OIDC disabled.</summary>
    public string OidcAuthority { get; set; } = "";

    /// <summary>OIDC client ID from the identity provider.</summary>
    public string OidcClientId { get; set; } = "";

    /// <summary>
    /// OIDC client secret from the identity provider.
    /// WARNING: Stored in plaintext in global.json — same security model as other secrets in this file.
    /// </summary>
    public string OidcClientSecret { get; set; } = "";

    /// <summary>
    /// Allowed email/subject for OIDC login. Single-user: only this identity can authenticate.
    /// Empty = accept any identity from the configured provider (use with caution).
    /// </summary>
    public string OidcAllowedEmail { get; set; } = "";

    // --- CORS ---

    /// <summary>Comma-separated allowed origins for CORS. "*" for development, specific origins for production.</summary>
    public string AllowedOrigins { get; set; } = "*";

    /// <summary>
    /// Automatically open the default browser when the web server starts.
    /// </summary>
    public bool OpenBrowserOnServerStart { get; set; } = false;

    /// <summary>
    /// Path to an alternate WebUI bundle folder. Empty string = use built-in (wwwroot).
    /// </summary>
    public string WebUIBundlePath { get; set; } = "";

    // --- Security Hardening ---

    /// <summary>Enable CSRF protection via Sec-Fetch-Site + Origin/Referer validation. Default: true.</summary>
    public bool EnableCsrfProtection { get; set; } = true;

    /// <summary>Enable Host header validation to prevent DNS rebinding attacks. Requires AllowedHostnames to be set. Default: false.</summary>
    public bool EnableHostHeaderValidation { get; set; } = false;

    /// <summary>Semicolon-delimited list of allowed hostnames for Host header validation. Supports wildcards (*.example.com).</summary>
    public string AllowedHostnames { get; set; } = "";

    /// <summary>Enable clickjacking protection via X-Frame-Options: DENY and CSP frame-ancestors. Default: true.</summary>
    public bool EnableClickjackingProtection { get; set; } = true;

    /// <summary>Enable ASP.NET Core ForwardedHeaders middleware for reverse proxy support. Requires TrustedProxies. Default: false.</summary>
    public bool EnableReverseProxySupport { get; set; } = false;

    /// <summary>Semicolon-delimited trusted proxy IPs or CIDR ranges (e.g. "10.0.0.1;172.16.0.0/12"). Required when EnableReverseProxySupport is true.</summary>
    public string TrustedProxies { get; set; } = "";

    /// <summary>Enable API key authentication via X-API-Key header. Keys managed via /auth/api-keys endpoints. Default: false.</summary>
    public bool ApiKeysEnabled { get; set; } = false;

    /// <summary>Number of failed login attempts before IP is temporarily banned. Default: 5.</summary>
    public int MaxAuthFailCount { get; set; } = 5;

    /// <summary>Duration in seconds to ban an IP after exceeding MaxAuthFailCount. Default: 3600 (1 hour).</summary>
    public int AuthBanDurationSeconds { get; set; } = 3600;

    /// <summary>Enable auth bypass for configured subnets (extends AllowLocalAccess beyond loopback). Default: false.</summary>
    public bool EnableSubnetAuthBypass { get; set; } = false;

    /// <summary>Semicolon-delimited CIDR subnets for auth bypass (e.g. "192.168.1.0/24"). Requires EnableSubnetAuthBypass.</summary>
    public string AuthBypassSubnets { get; set; } = "";

    /// <summary>Enable Secure, HttpOnly, SameSite=Strict flags on all cookies. Default: true.</summary>
    public bool EnableSecureCookie { get; set; } = true;

    /// <summary>When true, security violation responses include specific error codes for debugging. Default: false (generic 403).</summary>
    public bool VerboseSecurityErrors { get; set; } = false;

    /// <summary>Enable security response headers (X-Content-Type-Options, Referrer-Policy, Permissions-Policy, CSP). Default: true.</summary>
    public bool EnableSecurityHeaders { get; set; } = true;
}
