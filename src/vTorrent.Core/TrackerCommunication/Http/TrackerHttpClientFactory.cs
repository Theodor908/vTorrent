using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Network.Proxy;
using vTorrent.Core.TrackerCommunication;
using TrackerSettings = vTorrent.Abstractions.Settings.TrackerSettings;

namespace vTorrent.Core.TrackerCommunication.Http;

/// <summary>
/// Factory for creating optimized, pooled HttpClient instances for tracker communication.
/// Based on libtorrent's http_connection patterns and .NET best practices.
/// Uses SocketsHttpHandler for connection pooling and reuse.
/// </summary>
public class TrackerHttpClientFactory : IDisposable
{
    private readonly IOptionsMonitor<TrackerSettings> _trackerMonitor;
    private readonly IOptionsMonitor<ProxySettings>? _proxyMonitor;
    private readonly IOptionsMonitor<PrivacySettings>? _privacyMonitor;
    private readonly Lazy<HttpClient> _pooledClient;
    private readonly Lazy<SocketsHttpHandler> _handler;
    private bool _disposed;

    public TrackerHttpClientFactory(
        IOptionsMonitor<TrackerSettings> trackerMonitor,
        IOptionsMonitor<ProxySettings>? proxyMonitor = null,
        IOptionsMonitor<PrivacySettings>? privacyMonitor = null)
    {
        _trackerMonitor = trackerMonitor ?? throw new ArgumentNullException(nameof(trackerMonitor));
        _proxyMonitor = proxyMonitor;
        _privacyMonitor = privacyMonitor;

        _handler = new Lazy<SocketsHttpHandler>(CreateHandler, LazyThreadSafetyMode.ExecutionAndPublication);
        _pooledClient = new Lazy<HttpClient>(CreatePooledClient, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Gets a shared, connection-pooled HttpClient for tracker communication.
    /// This client should be reused across multiple requests for connection pooling benefits.
    /// </summary>
    public HttpClient GetClient()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TrackerHttpClientFactory));

        return _pooledClient.Value;
    }

    /// <summary>
    /// Creates a new HttpClient with the shared connection pool.
    /// Use this when you need a client with different timeout settings.
    /// </summary>
    public HttpClient CreateClient(TimeSpan? timeout = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TrackerHttpClientFactory));

        var client = new HttpClient(_handler.Value, disposeHandler: false)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(_trackerMonitor.CurrentValue.HttpTimeoutSeconds)
        };

        ConfigureClientDefaults(client);
        return client;
    }

    private SocketsHttpHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler
        {
            // Connection pooling settings
            PooledConnectionLifetime = TimeSpan.FromMinutes(TrackerConstants.PooledConnectionLifetimeMinutes),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(TrackerConstants.PooledConnectionIdleTimeoutMinutes),
            MaxConnectionsPerServer = TrackerConstants.MaxConnectionsPerServer,

            // Enable HTTP/2 if configured
            EnableMultipleHttp2Connections = TrackerConstants.EnableHttp2,

            // Auto-redirect settings
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,

            // Connection settings for performance
            ConnectTimeout = TimeSpan.FromSeconds(_trackerMonitor.CurrentValue.HttpTimeoutSeconds),

            // Keep connections alive
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),

            // Response drain timeout for connection reuse
            ResponseDrainTimeout = TimeSpan.FromSeconds(2),

            // Expect 100 Continue optimization (disabled for small tracker requests)
            Expect100ContinueTimeout = TimeSpan.Zero,

            // Automatic decompression
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        // Wire ValidateHttpsTrackers: when false, skip SSL certificate validation
        if (!_trackerMonitor.CurrentValue.ValidateHttpsTrackers)
        {
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            };
        }

        // Configure proxy (snapshot current settings at handler creation time)
        var proxySettings = _proxyMonitor?.CurrentValue;
        if (proxySettings != null && proxySettings.Type != ProxyType.None && proxySettings.ProxyTrackerConnections)
        {
            if (proxySettings.Type == ProxyType.Http || proxySettings.Type == ProxyType.HttpPassword)
            {
                // Native HTTP proxy support via SocketsHttpHandler
                var webProxy = new WebProxy(proxySettings.Hostname, proxySettings.Port);
                if (proxySettings.Type == ProxyType.HttpPassword)
                    webProxy.Credentials = new NetworkCredential(proxySettings.Username, proxySettings.Password);
                handler.Proxy = webProxy;
                handler.UseProxy = true;
            }
            else
            {
                // SOCKS proxy — use ConnectCallback (.NET 7+)
                handler.UseProxy = false;
                var proxyConnector = ProxyConnectorFactory.Create(proxySettings);
                if (proxyConnector != null)
                {
                    handler.ConnectCallback = async (context, ct) =>
                    {
                        var stream = await proxyConnector.ConnectThroughProxyAsync(
                            context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
                        return ((ProxyTransportStream)stream).AsNetworkStream();
                    };
                }
            }
        }
        else
        {
            handler.UseProxy = false;
        }

        return handler;
    }

    private HttpClient CreatePooledClient()
    {
        var client = new HttpClient(_handler.Value, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(_trackerMonitor.CurrentValue.HttpTimeoutSeconds)
        };

        ConfigureClientDefaults(client);
        return client;
    }

    private void ConfigureClientDefaults(HttpClient client)
    {
        // Set default headers for all requests
        client.DefaultRequestHeaders.ConnectionClose = false; // Keep connections alive
        client.DefaultRequestHeaders.UserAgent.Clear();
        var userAgent = _privacyMonitor?.CurrentValue?.AnonymousMode == true
            ? "" : _trackerMonitor.CurrentValue.UserAgent;
        if (!string.IsNullOrEmpty(userAgent))
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);

        // Accept all content types for tracker responses
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");

        // Set max response content buffer size
        client.MaxResponseContentBufferSize = TrackerConstants.MaxHttpResponseSize;
    }

    /// <summary>
    /// Gets statistics about the connection pool.
    /// </summary>
    public ConnectionPoolStatistics GetStatistics()
    {
        if (!_handler.IsValueCreated)
            return new ConnectionPoolStatistics(0, 0, 0);

        // Note: SocketsHttpHandler doesn't expose detailed pool statistics directly
        // This is a placeholder for when .NET adds such APIs
        return new ConnectionPoolStatistics(
            TrackerConstants.MaxConnectionsPerServer,
            TrackerConstants.PooledConnectionLifetimeMinutes,
            TrackerConstants.PooledConnectionIdleTimeoutMinutes
        );
    }

    public record ConnectionPoolStatistics(
        int MaxConnectionsPerServer,
        int PooledConnectionLifetimeMinutes,
        int PooledConnectionIdleTimeoutMinutes
    );

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_pooledClient.IsValueCreated)
            _pooledClient.Value.Dispose();

        if (_handler.IsValueCreated)
            _handler.Value.Dispose();
    }
}

/// <summary>
/// Singleton accessor for the tracker HTTP client factory.
/// Ensures connection pooling is shared across all tracker clients.
/// </summary>
public static class SharedTrackerHttpClient
{
    private static TrackerHttpClientFactory _factory;
    private static readonly object _lock = new();

    /// <summary>
    /// Initializes the shared HTTP client factory with the given settings.
    /// Call this once during application startup.
    /// </summary>
    public static void Initialize(IOptionsMonitor<TrackerSettings> trackerMonitor, IOptionsMonitor<ProxySettings>? proxyMonitor = null)
    {
        lock (_lock)
        {
            _factory?.Dispose();
            _factory = new TrackerHttpClientFactory(trackerMonitor, proxyMonitor);
        }
    }

    /// <summary>
    /// Gets the shared HTTP client for tracker communication.
    /// </summary>
    public static HttpClient GetClient()
    {
        lock (_lock)
        {
            if (_factory == null)
                throw new InvalidOperationException("SharedTrackerHttpClient has not been initialized. Call Initialize() first.");

            return _factory.GetClient();
        }
    }

    /// <summary>
    /// Gets the factory for creating custom clients.
    /// </summary>
    public static TrackerHttpClientFactory GetFactory()
    {
        lock (_lock)
        {
            if (_factory == null)
                throw new InvalidOperationException("SharedTrackerHttpClient has not been initialized. Call Initialize() first.");

            return _factory;
        }
    }

    /// <summary>
    /// Disposes the shared HTTP client factory.
    /// Call this during application shutdown.
    /// </summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            _factory?.Dispose();
            _factory = null;
        }
    }
}
