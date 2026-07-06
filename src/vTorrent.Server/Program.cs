using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using vTorrent.Abstractions.Interfaces.Auth;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Settings;
using ProfileManager = vTorrent.Core.Settings.ProfileManager;
using vTorrent.Server.Auth;
using vTorrent.Server.Hubs;
using vTorrent.Server.Middleware;

namespace vTorrent.Server;

public class Program
{
    // Entry point for standalone execution (future CLI daemon mode)
    public static async Task Main(string[] args)
    {
        // Standalone mode not yet implemented — server is started via StartAsync
        // from the Desktop app or CLI. This entry point satisfies the Web SDK requirement.
        Console.WriteLine("vTorrent.Server must be started via the Desktop app or CLI.");
        Console.WriteLine("Standalone daemon mode is not yet implemented.");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Configures and starts the vTorrent server.
    /// Called by the Desktop app or CLI when the server is enabled.
    /// </summary>
    /// <param name="connection">Shared SQLite connection (owned by SessionPersistence).</param>
    /// <param name="torrentService">Core torrent service for engine operations.</param>
    /// <param name="settingsManager">Settings manager for reading/writing global settings.</param>
    /// <param name="serverSettings">Snapshot of current ServerSettings.</param>
    /// <param name="connectionSettings">For port conflict check.</param>
    /// <param name="serverMonitor">Live settings monitor.</param>
    /// <param name="loggerFactory">Shared logger factory.</param>
    /// <param name="profileManager">Profile manager for reading/writing named settings profiles.</param>
    /// <param name="profileScheduler">Profile scheduler for time-based profile/mode switching.</param>
    /// <param name="cancellationToken">Cancellation token for shutdown.</param>
    public static async Task StartAsync(
        SqliteConnection connection,
        ITorrentService torrentService,
        SettingsManager settingsManager,
        ServerSettings serverSettings,
        ConnectionSettings connectionSettings,
        IOptionsMonitor<ServerSettings> serverMonitor,
        ILoggerFactory loggerFactory,
        ProfileManager? profileManager = null,
        ProfileScheduler? profileScheduler = null,
        string? webRootPath = null,
        CancellationToken cancellationToken = default)
    {
        var logger = loggerFactory.CreateLogger<Program>();

        // --- Startup guards ---

        // Port conflict guard
        if (serverSettings.ListenPort == connectionSettings.ListenPort)
        {
            logger.LogError("Server ListenPort {ServerPort} conflicts with BitTorrent peer ListenPort {PeerPort}. Server will not start.",
                serverSettings.ListenPort, connectionSettings.ListenPort);
            return;
        }

        // Password guard
        if (string.IsNullOrEmpty(serverSettings.LocalPasswordHash))
        {
            logger.LogError("Server cannot start: no password configured (LocalPasswordHash is empty)");
            return;
        }

        // JwtSecret guard — generate and persist if empty
        if (string.IsNullOrEmpty(serverSettings.JwtSecret))
        {
            var newSecret = JwtTokenService.GenerateJwtSecret();
            serverSettings.JwtSecret = newSecret;
            await settingsManager.UpdateAndSaveAsync(gs => gs.Server.JwtSecret = newSecret);
            logger.LogInformation("Generated and saved new JWT secret");
        }

        // --- Build ---

        // Resolve web root BEFORE creating the builder — ASP.NET Core 10 does not allow
        // changing web root via WebApplicationBuilder.WebHost after construction.
        string? resolvedWebRoot = null;
        if (!string.IsNullOrEmpty(webRootPath) && Directory.Exists(webRootPath))
        {
            resolvedWebRoot = webRootPath;
        }
        else
        {
            var assemblyDir = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
            var defaultWwwroot = Path.Combine(assemblyDir, "wwwroot");
            if (Directory.Exists(defaultWwwroot))
                resolvedWebRoot = defaultWwwroot;
        }

        var builder = resolvedWebRoot != null
            ? WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = resolvedWebRoot })
            : WebApplication.CreateBuilder();

        // Kestrel
        var scheme = serverSettings.EnableHttps ? "https" : "http";
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            var address = System.Net.IPAddress.Parse(serverSettings.ListenAddress);

            if (serverSettings.EnableHttps)
            {
                kestrel.Listen(address, serverSettings.ListenPort, listenOptions =>
                {
                    if (!string.IsNullOrEmpty(serverSettings.HttpsCertPath))
                        listenOptions.UseHttps(serverSettings.HttpsCertPath, serverSettings.HttpsCertPassword);
                    else
                        listenOptions.UseHttps();
                });
            }
            else
            {
                kestrel.Listen(address, serverSettings.ListenPort);
            }
        });

        // Logging
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(loggerFactory);

        // Auth services
        builder.Services.AddSingleton(serverMonitor);
        builder.Services.AddSingleton<JwtTokenService>();
        builder.Services.AddSingleton(new RefreshTokenRepository(connection, loggerFactory.CreateLogger<RefreshTokenRepository>()));

        // API Keys
        builder.Services.AddSingleton(new ApiKeyRepository(connection, loggerFactory.CreateLogger<ApiKeyRepository>()));
        builder.Services.AddSingleton<IApiKeyValidator>(sp => sp.GetRequiredService<ApiKeyRepository>());

        // IP Ban
        builder.Services.AddSingleton<IpBanTracker>();

        builder.Services.AddSingleton<PasswordHasher>();
        builder.Services.AddSingleton<OidcCallbackHandler>();

        // Core services (passed from host)
        builder.Services.AddSingleton(torrentService);
        builder.Services.AddSingleton(settingsManager);

        // Profile services (passed from host, optional — may be null if profiles not configured)
        if (profileManager != null)
            builder.Services.AddSingleton(profileManager);
        if (profileScheduler != null)
            builder.Services.AddSingleton(profileScheduler);
        if (profileManager != null)
        {
            builder.Services.AddSingleton(new ScheduleExporter(profileManager, settingsManager));
        }

        // Server services
        builder.Services.AddSingleton<Services.SettingsRedactor>();
        builder.Services.AddSingleton<Services.ServerTorrentService>();

        // Background services
        builder.Services.AddSingleton<Services.TorrentHubRelay>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Services.TorrentHubRelay>());
        builder.Services.AddHostedService<Services.TokenCleanupService>();

        // Controllers + SignalR
        // AddApplicationPart is required because when hosted by CLI/Desktop,
        // the entry assembly is NOT vTorrent.Server — MVC won't auto-discover
        // controllers in referenced assemblies without this.
        builder.Services.AddControllers(options =>
        {
            options.Filters.Add<Filters.ApiExceptionFilter>();
        }).AddApplicationPart(typeof(Program).Assembly)
          .AddJsonOptions(json =>
          {
              json.JsonSerializerOptions.NumberHandling =
                  System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
              json.JsonSerializerOptions.Converters.Add(
                  new System.Text.Json.Serialization.JsonStringEnumConverter());
          });
        builder.Services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(
                    new System.Text.Json.Serialization.JsonStringEnumConverter());
                // Allow Infinity/NaN in float/double fields (e.g., ratio calculations dividing by zero)
                options.PayloadSerializerOptions.NumberHandling =
                    System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
            });

        // JWT Bearer auth
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(serverSettings.JwtSecret))
                    {
                        KeyId = "vtorrent-signing-key"
                    },
                    ValidateIssuer = false,  // single server — add audience validation if multi-service
                    ValidateAudience = false,
                    ClockSkew = TimeSpan.Zero
                };

                // SignalR sends the access token via query string for WebSocket/SSE transports.
                // The JWT Bearer handler must read it from there for hub requests.
                // See: https://learn.microsoft.com/aspnet/core/signalr/authn-and-authz
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken) &&
                            path.StartsWithSegments("/hub"))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        // OIDC (conditional)
        if (!string.IsNullOrEmpty(serverSettings.OidcAuthority))
        {
            if (string.IsNullOrEmpty(serverSettings.OidcAllowedEmail))
                logger.LogWarning("OIDC is enabled but OidcAllowedEmail is empty — any identity from {Authority} can authenticate", serverSettings.OidcAuthority);

            builder.Services.AddAuthentication()
                .AddOpenIdConnect("oidc", options =>
                {
                    options.Authority = serverSettings.OidcAuthority;
                    options.ClientId = serverSettings.OidcClientId;
                    options.ClientSecret = serverSettings.OidcClientSecret;
                    options.ResponseType = "code";
                    options.UsePkce = true;
                    options.SaveTokens = true;
                    options.RequireHttpsMetadata = true;
                    options.Events = new OpenIdConnectEvents
                    {
                        OnRemoteFailure = context =>
                        {
                            logger.LogError(context.Failure, "OIDC authentication failed");
                            context.Response.Redirect("/auth/oidc/error");
                            context.HandleResponse();
                            return Task.CompletedTask;
                        }
                    };
                });
        }

        // CORS (SignalR-compatible — AllowCredentials requires specific origins or SetIsOriginAllowed)
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                if (serverSettings.AllowedOrigins == "*")
                {
                    policy.SetIsOriginAllowed(_ => true)
                          .AllowAnyMethod()
                          .AllowAnyHeader()
                          .AllowCredentials();
                }
                else
                {
                    policy.WithOrigins(serverSettings.AllowedOrigins.Split(',', StringSplitOptions.TrimEntries))
                          .AllowAnyMethod()
                          .AllowAnyHeader()
                          .AllowCredentials();
                }
            });
        });

        // Rate limiting
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.ContentType = "application/json";

                var retryAfter = 0;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterSpan))
                    retryAfter = (int)Math.Ceiling(retryAfterSpan.TotalSeconds);

                if (retryAfter > 0)
                    context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString();

                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    error = "Rate limit exceeded",
                    code = "RATE_LIMITED",
                    retryAfter
                });
                await context.HttpContext.Response.WriteAsync(json, cancellationToken);
            };

            options.AddSlidingWindowLimiter("auth", policy =>
            {
                policy.PermitLimit = 5;
                policy.Window = TimeSpan.FromMinutes(1);
                policy.SegmentsPerWindow = 6;
            });

            options.GlobalLimiter = PartitionedRateLimiter.Create<Microsoft.AspNetCore.Http.HttpContext, string>(context =>
            {
                var path = context.Request.Path.Value ?? "";

                // Static assets, favicon, and SignalR negotiate must never be rate-limited —
                // a single page load fires 30+ requests for JS/CSS chunks, images, and WebSocket setup.
                if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/favicon.svg", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/index.html", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/hub/", StringComparison.OrdinalIgnoreCase))
                {
                    return RateLimitPartition.GetNoLimiter("static");
                }

                return RateLimitPartition.GetSlidingWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 300,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6
                    });
            });
        });

        // Reverse Proxy (ForwardedHeaders) — must configure BEFORE Build()
        if (serverSettings.EnableReverseProxySupport)
        {
            if (string.IsNullOrWhiteSpace(serverSettings.TrustedProxies))
            {
                logger.LogWarning("Reverse proxy support enabled but TrustedProxies is empty — skipping");
            }
            else
            {
                builder.Services.Configure<ForwardedHeadersOptions>(options =>
                {
                    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                                             | ForwardedHeaders.XForwardedProto
                                             | ForwardedHeaders.XForwardedHost;
                    options.ForwardLimit = 1;
                    options.KnownProxies.Clear();
                    options.KnownIPNetworks.Clear();
                    foreach (var entry in serverSettings.TrustedProxies.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = entry.Trim();
                        if (trimmed.Contains('/'))
                            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(trimmed));
                        else
                            options.KnownProxies.Add(System.Net.IPAddress.Parse(trimmed));
                    }
                });
            }
        }

        // Host Header Validation
        if (serverSettings.EnableHostHeaderValidation && !string.IsNullOrEmpty(serverSettings.AllowedHostnames))
        {
            builder.Services.Configure<HostFilteringOptions>(options =>
            {
                options.AllowedHosts.Clear();
                foreach (var host in serverSettings.AllowedHostnames.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    options.AllowedHosts.Add(host.Trim());
                options.AllowEmptyHosts = false;
            });
        }

        // Cookie Security
        builder.Services.Configure<CookiePolicyOptions>(options =>
        {
            options.HttpOnly = Microsoft.AspNetCore.CookiePolicy.HttpOnlyPolicy.Always;
            options.Secure = serverSettings.EnableSecureCookie
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
            options.MinimumSameSitePolicy = SameSiteMode.Strict;
        });

        // --- Pipeline ---

        var app = builder.Build();

        // ForwardedHeaders (first — rewrites RemoteIpAddress before anything else reads it)
        if (serverSettings.EnableReverseProxySupport && !string.IsNullOrWhiteSpace(serverSettings.TrustedProxies))
            app.UseForwardedHeaders();

        // Host Header Validation
        if (serverSettings.EnableHostHeaderValidation && !string.IsNullOrEmpty(serverSettings.AllowedHostnames))
            app.UseHostFiltering();

        app.UseMiddleware<AccessTokenQueryMiddleware>();
        app.UseCors();

        // Security headers + cookie policy (after CORS, before static files)
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseCookiePolicy();

        // Static files BEFORE rate limiter — JS/CSS/images must never be rate-limited
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                var path = ctx.File.Name;
                if (string.Equals(path, "index.html", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Context.Response.Headers.CacheControl = "no-cache, no-store";
                }
                else
                {
                    // Hashed assets (JS/CSS) — cache for 1 year
                    ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                }
            }
        });
        app.UseRateLimiter();

        // CSRF (after rate limiter, before auth)
        app.UseMiddleware<CsrfOriginMiddleware>();

        app.UseAuthentication();

        // API key auth (after JWT auth, before LocalAccess)
        app.UseMiddleware<ApiKeyAuthMiddleware>();

        app.UseMiddleware<LocalAccessMiddleware>();

        // IP ban (after LocalAccess, before Authorization)
        app.UseMiddleware<IpBanMiddleware>();

        app.UseAuthorization();
        app.MapControllers();
        app.MapHub<TorrentHub>("/hub/torrent");
        app.MapFallbackToFile("index.html");

        logger.LogInformation("vTorrent server starting on {Scheme}://{Address}:{Port}",
            scheme, serverSettings.ListenAddress, serverSettings.ListenPort);

        await app.RunAsync(cancellationToken);
    }
}
